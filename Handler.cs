using EventLoggerPlugin;
using Gallop;
using Spectre.Console;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UmamusumeResponseAnalyzer.Game;
using UmamusumeResponseAnalyzer.Game.TurnInfo;
using UmamusumeResponseAnalyzer.LocalizedLayout.Handlers;
using static OnsenScenarioAnalyzer.i18n.Game;

namespace OnsenScenarioAnalyzer
{
    public static class Handler
    {
        // 理事长和记者的 Position ID
        private const int DIRECTOR_POSITION = 102;  // 理事长
        private const int REPORTER_POSITION = 103;  // 记者

        // Link 角色和卡片的映射关系
        // 东海帝王 - 砂质地层 +10%
        private static readonly int[] TOKAI_TEIO_IDS = [1003, 10003, 20047, 30003, 30058, 30111, 30140, 30213, 30275];
        // 创升 - 土质地层 +10%
        private static readonly int[] CHUANG_SHENG_IDS = [1080, 10125, 30249];
        // 美浦波旁 - 土质地层 +10%
        private static readonly int[] MEJIRO_BOURBON_IDS = [1026, 30277, 10032, 20009, 30059, 30066, 30121, 30141];
        // 奇锐骏 - 岩石地层 +10%
        private static readonly int[] KITASAN_BLACK_IDS = [1100, 10093, 30156, 30212];

        // 累计消耗体力统计
        private static int _lastVital = 0;  // 上一回合的体力值
        private static int _totalVitalConsumed = 0;  // 累计消耗体力

        /// <summary>
        /// 获取当前正在挖掘的温泉信息
        /// </summary>
        private static (int remainingLayers, int totalLayers, int currentRestVolume) GetCurrentOnsenDigInfo(SingleModeOnsenDataSet dataset)
        {
            if (dataset?.onsen_info_array == null)
                return (0, 0, 0);

            // 找到正在挖掘的温泉 (state == 2)
            var currentOnsen = dataset.onsen_info_array.FirstOrDefault(x => x.state == 2);
            if (currentOnsen == null || currentOnsen.stratum_info_array == null)
                return (0, 0, 0);

            // 计算总剩余挖掘量
            var totalRestVolume = currentOnsen.stratum_info_array.Sum(x => x.rest_volume);

            // 计算剩余层数（rest_volume > 0 的层数）
            var remainingLayers = currentOnsen.stratum_info_array.Count(x => x.rest_volume > 0);

            // 总层数
            var totalLayers = currentOnsen.stratum_info_array.Length;

            return (remainingLayers, totalLayers, totalRestVolume);
        }

        /// <summary>
        /// 计算训练的支援卡人头数（排除理事长和记者）
        /// </summary>
        private static int GetSupportCardCount(CommandInfo command)
        {
            return command.TrainingPartners.Count(x =>
                !x.IsNpc ||  // 支援卡（Position 1-6）
                (x.Position != DIRECTOR_POSITION && x.Position != REPORTER_POSITION)  // NPC 但不是理事长和记者
            );
        }

        /// <summary>
        /// 检测玩家是否携带指定的 剧本连接/link 角色或卡片
        /// </summary>
        private static bool HasLinkCharacterOrCard(TurnInfo turn, int[] linkIds)
        {
            // 检查角色 ID
            if (linkIds.Contains(turn.CharacterId))
                return true;

            // 检查支援卡 ID
            foreach (var supportCard in turn.SupportCards.Values)
            {
                if (linkIds.Contains(supportCard))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 获取 link 加成（根据地层类型）
        /// </summary>
        private static int GetLinkBonus(TurnInfo turn, int stratumType)
        {
            return stratumType switch
            {
                1 => HasLinkCharacterOrCard(turn, TOKAI_TEIO_IDS) ? 10 : 0,  // 砂质 - 东海帝王
                2 => (HasLinkCharacterOrCard(turn, CHUANG_SHENG_IDS) || HasLinkCharacterOrCard(turn, MEJIRO_BOURBON_IDS)) ? 10 : 0,  // 土质 - 创升或美浦波旁
                3 => HasLinkCharacterOrCard(turn, KITASAN_BLACK_IDS) ? 10 : 0,  // 岩石 - 奇锐骏
                _ => 0
            };
        }

        /// <summary>
        /// 获取当前温泉的挖掘力加成（包含 link 加成）
        /// </summary>
        private static int GetDigPower(SingleModeOnsenDataSet dataset, int stratumId, TurnInfo turn)
        {
            if (dataset?.dig_effect_info_array == null)
                return 0;

            var stratumType = GetStratumType(stratumId);

            // 查找对应地层类型的挖掘力加成
            var digEffect = dataset.dig_effect_info_array.FirstOrDefault(x => x.stratum_type == stratumType);
            var baseDigPower = digEffect?.dig_effect_value ?? 0;

            // 加上 link 加成
            var linkBonus = GetLinkBonus(turn, stratumType);

            return baseDigPower + linkBonus;
        }

        /// <summary>
        /// 根据地层ID获取地层类型
        /// </summary>
        private static int GetStratumType(int stratumId)
        {

            // 砂质: stratum_id = 4, 7, 9, 13
            // 土质: stratum_id = 5, 8, 11, 14
            // 岩石: stratum_id = 6, 10, 12, 15
            return stratumId switch
            {
                4 or 7 or 9 or 13 => 1,  // 砂质
                5 or 8 or 11 or 14 => 2,  // 土质
                6 or 10 or 12 or 15 => 3,  // 岩石
                _ => 0
            };
        }

        /// <summary>
        /// 计算训练的挖掘量（包含 link 加成）
        /// </summary>
        private static int CalculateDigAmount(int supportCardCount, SingleModeOnsenDataSet dataset, TurnInfo turn)
        {
            // 基础值 = 25 + 支援卡人头数
            var baseValue = 25 + supportCardCount;

            // 找到正在挖掘的温泉
            var currentOnsen = dataset?.onsen_info_array?.FirstOrDefault(x => x.state == 2);
            if (currentOnsen == null || currentOnsen.stratum_info_array == null)
                return 0;

            // 找到第一个未完成的地层
            var firstLayer = currentOnsen.stratum_info_array.FirstOrDefault(x => x.rest_volume > 0);
            if (firstLayer == null)
                return 0;

            var digPower = GetDigPower(dataset, firstLayer.stratum_id, turn);

            // 挖掘量 = floor(基础值 * ((100 + 挖掘力) / 100))
            var digAmount = (int)Math.Floor(baseValue * ((100 + digPower) / 100.0));

            // 如果会跨地层，需要计算第二层的挖掘量
            if (digAmount > firstLayer.rest_volume && currentOnsen.stratum_info_array.Length > 1)
            {
                // 第一层所需基础值 = ceil(第一层剩余 / (100 + 第一层挖掘力) * 100)
                var firstLayerNeededBase = (int)Math.Ceiling(firstLayer.rest_volume / ((100 + digPower) / 100.0));

                // 第二层挖掘量 = floor((基础值 - 第一层所需基础值) * (100 + 第二层挖掘力) / 100)
                var secondLayer = currentOnsen.stratum_info_array.Skip(1).FirstOrDefault(x => x.rest_volume > 0);
                if (secondLayer != null)
                {
                    var secondDigPower = GetDigPower(dataset, secondLayer.stratum_id, turn);
                    var secondLayerDig = (int)Math.Floor((baseValue - firstLayerNeededBase) * ((100 + secondDigPower) / 100.0));
                    digAmount = firstLayer.rest_volume + secondLayerDig;
                }
            }

            return digAmount;
        }

        public static int GetCommandInfoStage_legend(SingleModeCheckEventResponse @event)
        {
            //if ((@event.data.unchecked_event_array != null && @event.data.unchecked_event_array.Length > 0)) return;
            if (@event.data.chara_info.playing_state == 1 && (@event.data.unchecked_event_array == null || @event.data.unchecked_event_array.Length == 0))
            {
                return 2;
            } //常规训练
            else if (@event.data.chara_info.playing_state == 5 && @event.data.unchecked_event_array.Any(x => x.story_id == 400010112)) //选buff
            {
                return 5;
            }
            else if (@event.data.chara_info.playing_state == 5 &&
                (@event.data.unchecked_event_array.Any(x => x.story_id == 830241003))) //选团卡事件
            {
                return 3;
            }
            else
            {
                return 0;
            }
        }
        public static void ParseOnsenCommandInfo(SingleModeCheckEventResponse @event)
        {
            var stage = GetCommandInfoStage_legend(@event);
            if (stage == 0)
                return;
            var layout = new Layout().SplitColumns(
                new Layout("Main").Size(CommandInfoLayout.Current.MainSectionWidth).SplitRows(
                    new Layout("体力干劲条").SplitColumns(
                        new Layout("日期").Ratio(4),
                        new Layout("总属性").Ratio(6),
                        new Layout("体力").Ratio(6),
                        new Layout("干劲").Ratio(3)).Size(3),
                    new Layout("重要信息").Size(5),
                    new Layout("温泉信息").SplitColumns(
                        new Layout("温泉券").Ratio(1),
                        new Layout("温泉buff").Ratio(1),
                        new Layout("累计消耗体力").Ratio(1),
                        new Layout("超回复").Ratio(1)
                        ).Size(3),
                    //new Layout("分割", new Rule()).Size(1),
                    new Layout("训练信息")  // size 20, 共约30行
                    ).Ratio(4),
                new Layout("Ext").Ratio(1)
                );
            var noTrainingTable = false;
            var critInfos = new List<string>();
            var turn = new TurnInfoOnsen(@event.data);
            var dataset = @event.data.onsen_data_set;

            if (GameStats.currentTurn != turn.Turn - 1 //正常情况
                && GameStats.currentTurn != turn.Turn //重复显示
                && turn.Turn != 1 //第一个回合
                )
            {
                GameStats.isFullGame = false;
                critInfos.Add(string.Format(I18N_WrongTurnAlert, GameStats.currentTurn, turn.Turn));
                EventLogger.Init(@event);
            }
            else if (turn.Turn == 1)
            {
                GameStats.isFullGame = true;
                EventLogger.Init(@event);
            }

            //买技能，大师杯剧本年末比赛，会重复显示
            if (@event.data.chara_info.playing_state != 1)
            {
                critInfos.Add(I18N_RepeatTurn);
            }
            else
            {
                //初始化TurnStats
                GameStats.whichScenario = @event.data.chara_info.scenario_id;
                GameStats.currentTurn = turn.Turn;
                GameStats.stats[turn.Turn] = new TurnStats();

                // 安全调用 EventLogger.Update，避免空引用异常
                try
                {
                    EventLogger.Update(@event);
                }
                catch (ArgumentNullException)
                {
                    // EventLogger 未正确初始化，忽略此错误
                    // 这通常发生在从中通开始打开小黑板时 EventLoggerPlugin 里 lastProper 可能为 null
                }
            }
            var trainItems = new Dictionary<int, SingleModeCommandInfo>
            {
                { 101, @event.data.home_info.command_info_array[0] },
                { 105, @event.data.home_info.command_info_array[1] },
                { 102, @event.data.home_info.command_info_array[2] },
                { 103, @event.data.home_info.command_info_array[3] },
                { 106, @event.data.home_info.command_info_array[4] }
            };
            var trainStats = new TrainStats[5];
            var turnStat = @event.data.chara_info.playing_state != 1 ? new TurnStats() : GameStats.stats[turn.Turn];
            turnStat.motivation = @event.data.chara_info.motivation;
            var failureRate = new Dictionary<int, int>();

            // 总属性计算
            var currentFiveValue = new int[]
            {
                @event.data.chara_info.speed,
                @event.data.chara_info.stamina,
                @event.data.chara_info.power ,
                @event.data.chara_info.guts ,
                @event.data.chara_info.wiz ,
            };
            var fiveValueMaxRevised = new int[]
            {
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_speed),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_stamina),
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_power) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_guts) ,
                ScoreUtils.ReviseOver1200(@event.data.chara_info.max_wiz) ,
            };
            var currentFiveValueRevised = currentFiveValue.Select(ScoreUtils.ReviseOver1200).ToArray();
            var totalValue = currentFiveValueRevised.Sum();
            var totalValueWithPt = totalValue + @event.data.chara_info.skill_point;

            for (var i = 0; i < 5; i++)
            {
                var trainId = TurnInfoOnsen.TrainIds[i];
                failureRate[trainId] = trainItems[trainId].failure_rate;
                var trainParams = new Dictionary<int, int>()
                {
                    {1,0},
                    {2,0},
                    {3,0},
                    {4,0},
                    {5,0},
                    {30,0},
                    {10,0},
                };
                foreach (var item in turn.GetCommonResponse().home_info.command_info_array)
                {
                    if (TurnInfoOnsen.ToTrainId.TryGetValue(item.command_id, out var value) && value == trainId)
                    {
                        foreach (var trainParam in item.params_inc_dec_info_array)
                            trainParams[trainParam.target_type] += trainParam.value;
                    }
                }

                var stats = new TrainStats
                {
                    FailureRate = trainItems[trainId].failure_rate,
                    VitalGain = trainParams[10]
                };
                if (turn.Vital + stats.VitalGain > turn.MaxVital)
                    stats.VitalGain = turn.MaxVital - turn.Vital;
                if (stats.VitalGain < -turn.Vital)
                    stats.VitalGain = -turn.Vital;
                stats.FiveValueGain = [trainParams[1], trainParams[2], trainParams[3], trainParams[4], trainParams[5]];
                stats.PtGain = trainParams[30];

                var valueGainUpper = dataset.command_info_array.FirstOrDefault(x => x.command_id == trainId || x.command_id == TurnInfoOnsen.XiahesuIds[trainId])?.params_inc_dec_info_array;
                if (valueGainUpper != null)
                {
                    foreach (var item in valueGainUpper)
                    {
                        if (item.target_type == 30)
                            stats.PtGain += item.value;
                        else if (item.target_type <= 5)
                            stats.FiveValueGain[item.target_type - 1] += item.value;
                    }
                }

                for (var j = 0; j < 5; j++)
                    stats.FiveValueGain[j] = ScoreUtils.ReviseOver1200(turn.Stats[j] + stats.FiveValueGain[j]) - ScoreUtils.ReviseOver1200(turn.Stats[j]);

                if (turn.Turn == 1)
                {
                    turnStat.trainLevel[i] = 1;
                    turnStat.trainLevelCount[i] = 0;
                }
                else
                {
                    var lastTrainLevel = GameStats.stats[turn.Turn - 1] != null ? GameStats.stats[turn.Turn - 1].trainLevel[i] : 1;
                    var lastTrainLevelCount = GameStats.stats[turn.Turn - 1] != null ? GameStats.stats[turn.Turn - 1].trainLevelCount[i] : 0;

                    turnStat.trainLevel[i] = lastTrainLevel;
                    turnStat.trainLevelCount[i] = lastTrainLevelCount;
                    if (GameStats.stats[turn.Turn - 1] != null &&
                        GameStats.stats[turn.Turn - 1].playerChoice == TurnInfoOnsen.TrainIds[i] &&
                        !GameStats.stats[turn.Turn - 1].isTrainingFailed &&
                        !((turn.Turn - 1 >= 37 && turn.Turn - 1 <= 40) || (turn.Turn - 1 >= 61 && turn.Turn - 1 <= 64))
                        )//上回合点的这个训练，计数+1
                        turnStat.trainLevelCount[i] += 1;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }
                    //检查是否有剧本全体训练等级+1
                    if (turn.Turn == 25 || turn.Turn == 37 || turn.Turn == 49)
                        turnStat.trainLevelCount[i] += 4;
                    if (turnStat.trainLevelCount[i] >= 4)
                    {
                        turnStat.trainLevelCount[i] -= 4;
                        turnStat.trainLevel[i] += 1;
                    }

                    if (turnStat.trainLevel[i] >= 5)
                    {
                        turnStat.trainLevel[i] = 5;
                        turnStat.trainLevelCount[i] = 0;
                    }

                    var trainlv = @event.data.chara_info.training_level_info_array.First(x => x.command_id == TurnInfoOnsen.TrainIds[i]).level;
                    if (turnStat.trainLevel[i] != trainlv && stage == 2)
                    {
                        //可能是半途开启小黑板，也可能是有未知bug
                        critInfos.Add($"[red]警告：训练等级预测错误，预测{TurnInfoOnsen.TrainIds[i]}为lv{turnStat.trainLevel[i]}(+{turnStat.trainLevelCount[i]})，实际为lv{trainlv}[/]");
                        turnStat.trainLevel[i] = trainlv;
                        turnStat.trainLevelCount[i] = 0;//如果是半途开启小黑板，则会在下一次升级时变成正确的计数
                    }
                }

                trainStats[i] = stats;
            }
            if (stage == 2)
            {
                // 把训练等级信息更新到GameStats
                turnStat.fiveTrainStats = trainStats;
                GameStats.stats[turn.Turn] = turnStat;
            }

            //训练或比赛阶段
            if (stage == 2)
            {
                var grids = new Grid();
                grids.AddColumns(6);
                foreach (var column in grids.Columns)
                {
                    column.Padding = new Padding(0, 0, 0, 0);
                }

                var failureRateStr = new string[5];
                //失败率>=40%标红、>=20%(有可能大失败)标DarkOrange、>0%标黄
                for (var i = 0; i < 5; i++)
                {
                    var thisFailureRate = failureRate[TurnInfoOnsen.TrainIds[i]];
                    failureRateStr[i] = thisFailureRate switch
                    {
                        >= 40 => $"[red]({thisFailureRate}%)[/]",
                        >= 20 => $"[darkorange]({thisFailureRate}%)[/]",
                        > 0 => $"[yellow]({thisFailureRate}%)[/]",
                        _ => string.Empty
                    };
                }

                // 获取温泉挖掘信息
                var (remainingLayers, totalLayers, currentRestVolume) = GetCurrentOnsenDigInfo(dataset);
                var commands = turn.CommandInfoArray.Select(command =>
                {
                    var table = new Table()
                    .AddColumn(command.TrainIndex switch
                    {
                        1 => $"{I18N_Speed}{failureRateStr[0]}",
                        2 => $"{I18N_Stamina}{failureRateStr[1]}",
                        3 => $"{I18N_Power}{failureRateStr[2]}",
                        4 => $"{I18N_Nuts}{failureRateStr[3]}",
                        5 => $"{I18N_Wiz}{failureRateStr[4]}",
                        6 => $"PR活动"
                    });

                    var currentStat = turn.StatsRevised[command.TrainIndex - 1];
                    var statUpToMax = turn.MaxStatsRevised[command.TrainIndex - 1] - currentStat;
                    table.AddRow(I18N_CurrentRemainStat);
                    table.AddRow($"{currentStat}:{statUpToMax switch
                    {
                        > 400 => $"{statUpToMax}",
                        > 200 => $"[yellow]{statUpToMax}[/]",
                        _ => $"[red]{statUpToMax}[/]"
                    }}");
                    table.AddRow(new Rule());

                    var afterVital = trainStats[command.TrainIndex - 1].VitalGain + turn.Vital;
                    table.AddRow(afterVital switch
                    {
                        < 30 => $"{I18N_Vital}:[red]{afterVital}[/]/{turn.MaxVital}",
                        < 50 => $"{I18N_Vital}:[darkorange]{afterVital}[/]/{turn.MaxVital}",
                        < 70 => $"{I18N_Vital}:[yellow]{afterVital}[/]/{turn.MaxVital}",
                        _ => $"{I18N_Vital}:[green]{afterVital}[/]/{turn.MaxVital}"
                    });

                    // 显示温泉挖掘进度和挖掘量
                    if (remainingLayers > 0)
                    {
                        // 计算支援卡人头数（排除理事长和记者）
                        var supportCardCount = GetSupportCardCount(command);
                        // 计算挖掘量（包含 link 加成）
                        var digAmount = CalculateDigAmount(supportCardCount, dataset, turn);

                        table.AddRow($"{I18N_OnsenDigging}: {remainingLayers}/{totalLayers}{I18N_OnsenLayer} | 挖掘:{digAmount}/{currentRestVolume}");
                    }
                    else
                    {
                        table.AddRow($"{I18N_OnsenDigging}: {I18N_OnsenCompleted}");
                    }

                    turn.PointGainInfoDictionary.TryGetValue(command.CommandId, out var gain);
                    table.AddRow($"Lv{command.TrainLevel} | {gain}");
                    table.AddRow(new Rule());

                    var stats = trainStats[command.TrainIndex - 1];
                    var score = stats.FiveValueGain.Sum();
                    if (score == trainStats.Max(x => x.FiveValueGain.Sum()))
                        table.AddRow($"{I18N_StatSimple}:[aqua]{score}[/]|Pt:{stats.PtGain}");
                    else
                        table.AddRow($"{I18N_StatSimple}:{score}|Pt:{stats.PtGain}");

                    foreach (var trainingPartner in command.TrainingPartners)
                    {
                        table.AddRow(trainingPartner.Name);
                        if (trainingPartner.Shining)
                            table.BorderColor(Color.LightGreen);
                    }
                    for (var i = 5 - command.TrainingPartners.Count(); i > 0; i--)
                    {
                        table.AddRow(string.Empty);
                    }
                    table.AddRow(new Rule());

                    return new Padder(table).Padding(0, 0, 0, 0);
                }).ToList();
                grids.AddRow([.. commands]);

                layout["训练信息"].Update(grids);
            }
            else
            {
                var grids = new Grid();
                grids.AddColumns(1);
                grids.AddRow([$"非训练阶段，stage={stage}"]);
                layout["训练信息"].Update(grids);
                noTrainingTable = true;
            }

            // 额外信息
            var exTable = new Table().AddColumn("Extras");
            exTable.HideHeaders();
            // 计算连续事件表现
            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            if (eventPerf.Count > 0)
            {
                exTable.AddRow(new Rule());
                foreach (var row in eventPerf)
                    exTable.AddRow(new Markup(row));
            }

            layout["日期"].Update(new Panel($"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth}").Expand());
            layout["总属性"].Update(new Panel($"[cyan]总属性: {totalValue}[/]").Expand());
            layout["体力"].Update(new Panel($"{I18N_Vital}: [green]{turn.Vital}[/]/{turn.MaxVital}").Expand());
            layout["干劲"].Update(new Panel(@event.data.chara_info.motivation switch
            {
                // 换行分裂和箭头符号有关，去掉
                5 => $"[green]{I18N_MotivationBest}[/]",
                4 => $"[yellow]{I18N_MotivationGood}[/]",
                3 => $"[red]{I18N_MotivationNormal}[/]",
                2 => $"[red]{I18N_MotivationBad}[/]",
                1 => $"[red]{I18N_MotivationWorst}[/]"
            }).Expand());

            var availableTrainingCount = @event.data.home_info.command_info_array.Count(x => x.is_enable == 1);
            if (availableTrainingCount <= 1)
            {
                critInfos.Add("[aqua]非训练回合[/]");
            }
            layout["重要信息"].Update(new Panel(string.Join(Environment.NewLine, critInfos)).Expand());

            // 温泉券信息
            var ticketNum = dataset.bathing_info.ticket_num;
            layout["温泉券"].Update(new Panel($"{I18N_OnsenTicket}: {ticketNum}/3").Expand());

            // 温泉buff持续时间
            var onsenBuffRemain = dataset.bathing_info.onsen_effect_remain_count;
            layout["温泉buff"].Update(new Panel($"温泉Buff: {onsenBuffRemain}回合").Expand());

            // 累计消耗体力统计
            var currentVital = @event.data.chara_info.vital;
            var currentTurn = turn.Turn;

            // 第一回合初始化
            if (currentTurn == 1)
            {
                _lastVital = currentVital;
                _totalVitalConsumed = 0;
            }
            // 从第三回合开始统计
            else if (currentTurn >= 3)
            {
                // 只统计减少的体力（每次体力减少时立即累加）
                if (currentVital < _lastVital)
                {
                    _totalVitalConsumed += _lastVital - currentVital;
                }
            }
            // 第二回合也需要更新 _lastVital，但不统计

            // 每次都更新 _lastVital 为当前值
            _lastVital = currentVital;

            // 显示累计消耗体力
            var vitalDisplay = currentTurn < 3 ? "❎" : _totalVitalConsumed.ToString();
            layout["累计消耗体力"].Update(new Panel($"累计消耗体力: {vitalDisplay}").Expand());

            // 超回复状态
            var superiorState = dataset.bathing_info.superior_state;
            layout["超回复"].Update(new Panel(superiorState == 1 ? $"[green]{I18N_CanSuperiorRecovery}[/]" : $"[grey]{I18N_CannotSuperiorRecovery}[/]").Expand());

            layout["Ext"].Update(exTable);

            GameStats.Print();

            AnsiConsole.Write(layout);
            // 光标倒转一点
            if (noTrainingTable)
                AnsiConsole.Cursor.SetPosition(0, 15);
            else
                AnsiConsole.Cursor.SetPosition(0, 31);
        }
    }
}
