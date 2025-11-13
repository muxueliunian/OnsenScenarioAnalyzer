using EventLoggerPlugin;
using Gallop;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UmamusumeResponseAnalyzer;
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

        // Link 角色 ID（通过角色 ID 匹配，自动覆盖该角色的所有支援卡）
        // 东海帝王 - 砂质地层 +10%
        private static readonly int[] TOKAI_TEIO_CHARA_IDS = [1003];
        // 创升 - 土质地层 +10%
        private static readonly int[] CHUANG_SHENG_CHARA_IDS = [1080];
        // 美浦波旁 - 土质地层 +10%
        private static readonly int[] MEJIRO_BOURBON_CHARA_IDS = [1026];
        // 奇锐骏 - 岩石地层 +10%
        private static readonly int[] KITASAN_BLACK_CHARA_IDS = [1100];

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

        /// <summary>
        /// 检测玩家是否携带指定的 剧本连接/link 角色
        /// </summary>
        /// <param name="turn">当前回合信息</param>
        /// <param name="linkCharaIds">Link 角色 ID 数组</param>
        /// <returns>如果携带了指定角色或该角色的任意支援卡，返回 true</returns>
        /// <remarks>
        /// 通过将支援卡 ID 转换为角色 ID 进行匹配，自动覆盖该角色的所有支援卡（R、SR、SSR、活动卡等）
        /// </remarks>
        private static bool HasLinkCharacterOrCard(TurnInfo turn, int[] linkCharaIds)
        {
            // 检查育成角色 ID
            if (linkCharaIds.Contains(turn.CharacterId))
                return true;

            // 检查支援卡对应的角色 ID
            foreach (var supportCardId in turn.SupportCards.Values)
            {
                // 将支援卡 ID 转换为角色 ID
                var charaId = Database.Names.GetSupportCard(supportCardId).CharaId;
                if (linkCharaIds.Contains(charaId))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 获取 Link 加成（根据地层类型）
        /// </summary>
        /// <param name="turn">当前回合信息</param>
        /// <param name="stratumType">地层类型（1=砂质, 2=土质, 3=岩石）</param>
        /// <returns>Link 加成百分比（0 或 10）</returns>
        private static int GetLinkBonus(TurnInfo turn, int stratumType)
        {
            return stratumType switch
            {
                1 => HasLinkCharacterOrCard(turn, TOKAI_TEIO_CHARA_IDS) ? 10 : 0,  // 砂质 - 东海帝王
                2 => (HasLinkCharacterOrCard(turn, CHUANG_SHENG_CHARA_IDS) || HasLinkCharacterOrCard(turn, MEJIRO_BOURBON_CHARA_IDS)) ? 10 : 0,  // 土质 - 创升或美浦波旁
                3 => HasLinkCharacterOrCard(turn, KITASAN_BLACK_CHARA_IDS) ? 10 : 0,  // 岩石 - 奇锐骏
                _ => 0
            };
        }

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
        /// 根据地层ID获取地层类型
        /// </summary>
        private static int GetStratumType(int stratumId)
        {
            // 砂质: stratum_id = 4, 7, 9, 13, 15, 18
            // 土质: stratum_id = 5, 8, 11, 14, 16, 19
            // 岩石: stratum_id = 6, 10, 12, 17, 20
            return stratumId switch
            {
                4 or 7 or 9 or 13 or 15 or 18 => 1,  // 砂质
                5 or 8 or 11 or 14 or 16 or 19 => 2,  // 土质
                6 or 10 or 12 or 17 or 20 => 3,  // 岩石
                _ => 0
            };
        }

        /// <summary>
        /// 获取当前温泉的挖掘力加成（直接从 dig_effect_info_array 获取，已包含所有加成）
        /// </summary>
        private static int GetDigPower(SingleModeOnsenDataSet dataset, int stratumId, TurnInfo turn)
        {
            if (dataset?.dig_effect_info_array == null || dataset.dig_effect_info_array.Length < 3)
                return 0;

            var stratumType = GetStratumType(stratumId);

            // 根据地层类型选择对应的挖掘力加成
            // stratumType = 1（砂质）-> dig_effect_info_array[0]
            // stratumType = 2（土质）-> dig_effect_info_array[1]
            // stratumType = 3（岩石）-> dig_effect_info_array[2]
            var index = stratumType - 1;
            if (index < 0 || index >= dataset.dig_effect_info_array.Length)
                return 0;

            // dig_effect_value 已经包含了所有加成（基础 + Link + 其他），直接使用
            return dataset.dig_effect_info_array[index].dig_effect_value;
        }

        /// <summary>
        /// 计算训练的挖掘量（包含 link 加成和跨地层计算）
        /// </summary>
        private static int CalculateDigAmount(int supportCardCount, SingleModeOnsenDataSet dataset, TurnInfo turn)
        {
            // 基础值 = 25 + 支援卡人头数
            var baseValue = 25 + supportCardCount;

            // 找到正在挖掘的温泉
            var currentOnsen = dataset?.onsen_info_array?.FirstOrDefault(x => x.state == 2);
            if (currentOnsen == null || currentOnsen.stratum_info_array == null)
                return 0;

            // 找到第一个未完成的地层（当前正在挖的地层）
            var firstLayerIndex = -1;
            for (var i = 0; i < currentOnsen.stratum_info_array.Length; i++)
            {
                if (currentOnsen.stratum_info_array[i].rest_volume > 0)
                {
                    firstLayerIndex = i;
                    break;
                }
            }

            if (firstLayerIndex == -1)
                return 0;  // 所有地层都已挖完

            var firstLayer = currentOnsen.stratum_info_array[firstLayerIndex];
            var digPower = GetDigPower(dataset, firstLayer.stratum_id, turn);

            // 挖掘量 = floor(基础值 * ((100 + 挖掘力) / 100))
            var digAmount = (int)Math.Floor(baseValue * ((100 + digPower) / 100.0));

            // 如果会跨地层，需要计算第二层的挖掘量
            if (digAmount > firstLayer.rest_volume)
            {
                // 第一层所需基础值 = floor(第一层剩余 / (100 + 第一层挖掘力) * 100)
                var firstLayerNeededBase = (int)Math.Floor(firstLayer.rest_volume / ((100 + digPower) / 100.0));

                // 找到下一层（从当前层的下一个索引开始找第一个 rest_volume > 0 的地层）
                for (var i = firstLayerIndex + 1; i < currentOnsen.stratum_info_array.Length; i++)
                {
                    var nextLayer = currentOnsen.stratum_info_array[i];
                    if (nextLayer.rest_volume > 0)
                    {
                        // 第二层挖掘量 = floor((基础值 - 第一层所需基础值) * (100 + 第二层挖掘力) / 100)
                        var secondDigPower = GetDigPower(dataset, nextLayer.stratum_id, turn);
                        var secondLayerDig = (int)Math.Floor((baseValue - firstLayerNeededBase) * ((100 + secondDigPower) / 100.0));
                        digAmount = firstLayer.rest_volume + secondLayerDig;
                        break;  // 只计算到下一层，不继续往下
                    }
                }
            }

            return digAmount;
        }

        /// <summary>
        /// 计算加权平均挖掘力（根据剩余地层类型分布）
        /// </summary>
        /// <param name="dataset">温泉数据集</param>
        /// <param name="turn">当前回合信息</param>
        /// <returns>加权平均挖掘力（百分比），如果无法计算返回 0</returns>
        private static int CalculateWeightedDigPower(SingleModeOnsenDataSet dataset, TurnInfo turn)
        {
            var currentOnsen = dataset?.onsen_info_array?.FirstOrDefault(x => x.state == 2);
            if (currentOnsen == null || currentOnsen.stratum_info_array == null)
                return 0;

            // 统计各类型地层的剩余量
            var sandVolume = 0;
            var soilVolume = 0;
            var rockVolume = 0;

            foreach (var stratum in currentOnsen.stratum_info_array)
            {
                if (stratum.rest_volume <= 0)
                    continue;

                var type = GetStratumType(stratum.stratum_id);
                switch (type)
                {
                    case 1:
                        sandVolume += stratum.rest_volume;
                        break;
                    case 2:
                        soilVolume += stratum.rest_volume;
                        break;
                    case 3:
                        rockVolume += stratum.rest_volume;
                        break;
                }
            }

            var totalVolume = sandVolume + soilVolume + rockVolume;
            if (totalVolume == 0)
                return 0;

            // 获取各类型的挖掘力（包含 link 加成）
            // 使用代表性的 stratum_id：砂质=4, 土质=5, 岩石=6
            var sandPower = GetDigPower(dataset, 4, turn);
            var soilPower = GetDigPower(dataset, 5, turn);
            var rockPower = GetDigPower(dataset, 6, turn);

            // 加权平均
            var weightedPower = (sandPower * sandVolume + soilPower * soilVolume + rockPower * rockVolume) / totalVolume;

            return weightedPower;
        }

        /// <summary>
        /// 预测温泉挖掘完成所需回合数（方案1：简单粗暴法）
        /// </summary>
        /// <param name="dataset">温泉数据集</param>
        /// <param name="turn">当前回合信息</param>
        /// <param name="assumedSupportCardCount">假设的平均支援卡人头数（默认 2）</param>
        /// <param name="trainingRate">假设的训练频率（默认 0.75，即 75%）</param>
        /// <returns>预计完成回合数，如果已完成返回 0，如果无法计算返回 -1</returns>
        private static int PredictRemainingTurns(
            SingleModeOnsenDataSet dataset,
            TurnInfo turn,
            int assumedSupportCardCount = 2,
            float trainingRate = 0.75f)
        {
            // 获取当前温泉信息
            var currentOnsen = dataset?.onsen_info_array?.FirstOrDefault(x => x.state == 2);
            if (currentOnsen == null || currentOnsen.stratum_info_array == null)
                return -1;

            // 计算总剩余挖掘量
            var totalRestVolume = currentOnsen.stratum_info_array.Sum(x => x.rest_volume);
            if (totalRestVolume <= 0)
                return 0;  // 已完成

            // 计算剩余回合数
            var currentTurn = turn.Turn;
            var remainingTurns = 78 - currentTurn;
            if (remainingTurns <= 0)
                return -1;  // 已经没有剩余回合

            // 计算加权平均挖掘力
            var weightedDigPower = CalculateWeightedDigPower(dataset, turn);

            // 计算平均每次训练挖掘量
            // 基础值 = 25 + 假设人头数
            var baseValue = 25 + assumedSupportCardCount;
            // 平均每次挖掘量 = floor(基础值 * ((100 + 挖掘力) / 100))
            var avgDigPerTraining = (int)Math.Floor(baseValue * ((100 + weightedDigPower) / 100.0));
            if (avgDigPerTraining <= 0)
                return -1;  // 无法计算

            // 预测需要的训练次数
            var predictedTrainingCount = (int)Math.Ceiling(totalRestVolume / (double)avgDigPerTraining);

            // 预测回合数 = ceil(训练次数 / 训练频率)
            var predictedTurns = (int)Math.Ceiling(predictedTrainingCount / trainingRate);

            return predictedTurns;
        }



        // 当前生效的温泉buff是否为超回复
        public static bool isCurrentBuffSuper = false;
        // 上次的温泉buff情况
        public static SingleModeOnsenBathingInfo lastBathing = new();
        // 上次的事件数量
        public static int lastEventCount = 0;
        // 给超回复的事件ID
        public static int[] superEvents = { 809050011, 809050012, 809050013, 809050014, 809050015 };
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
                    new Layout("剧本信息").SplitColumns(
                        new Layout("温泉券").Ratio(1),
                        new Layout("温泉Buff").Ratio(1),
                        new Layout("超回复").Ratio(1),
                        new Layout("挖掘进度").Ratio(1)
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
                EventLogger.IsStart = true;
                isCurrentBuffSuper = false;
                lastEventCount = 0;
                // 初始化时根据温泉buff状态设置是否记录体力消耗
                if (turn.Turn <= 2 || turn.Turn > 72)
                {
                    EventLogger.captureVitalSpending = false;
                }
                else
                {
                    EventLogger.captureVitalSpending = true;
                }
            }
            else if (turn.Turn == 1)
            {
                GameStats.isFullGame = true;
                EventLogger.Init(@event);
                EventLogger.IsStart = true;
                isCurrentBuffSuper = false;
                lastEventCount = 0;
            }

            // 统计上回合事件
            var lastEvents = EventLogger.AllEvents
                    .Skip(lastEventCount)
                    .Select(x => x.StoryId)
                    .ToList();
            lastEventCount = EventLogger.AllEvents.Count;
            // 统计温泉Buff情况
            var bathing = dataset.bathing_info;
            if (bathing != null)
            {
                // 更新跟踪状态
                if (lastBathing.superior_state == 0 && bathing.superior_state > 0)
                {
                    EventLogger.captureVitalSpending = false;
                    if (lastEvents.Any(x => superEvents.Contains(x)))
                    {
                        AnsiConsole.MarkupLine("[magenta]友人提供超回复[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[magenta]触发超回复[/]");
                        EventLogger.vitalSpent = 0;
                    }                    
                }
                if (lastBathing.onsen_effect_remain_count == 0 && bathing.onsen_effect_remain_count == 2)
                {
                    AnsiConsole.MarkupLine("[magenta]使用温泉Buff[/]");
                    if (lastBathing.superior_state > 0) isCurrentBuffSuper = true;
                }
                if (isCurrentBuffSuper && bathing.onsen_effect_remain_count == 0 && bathing.superior_state == 0)
                {
                    AnsiConsole.MarkupLine("[magenta]超回复Buff结束，开始记录体力消耗[/]");
                    isCurrentBuffSuper = false;
                    EventLogger.captureVitalSpending = true;
                }
                lastBathing = bathing;

                // 显示当前状态
                layout["温泉券"].Update(new Panel($"[cyan]温泉券: {bathing.ticket_num} / 3[/]").Expand());
                if (bathing.onsen_effect_remain_count > 0)
                {
                    layout["温泉Buff"].Update(new Panel($"[lightgreen]温泉Buff剩余 {bathing.onsen_effect_remain_count} 回合[/]").Expand());
                }
                else
                {
                    layout["温泉Buff"].Update(new Panel($"温泉Buff未生效").Expand());
                }
                if (isCurrentBuffSuper) {
                    layout["超回复"].Update(new Panel($"[lightgreen]超回复生效中[/]").Expand());
                }
                else if (bathing.superior_state > 0)
                {
                    layout["超回复"].Update(new Panel($"[green]必定超回复[/]").Expand());
                }
                else
                {
                    layout["超回复"].Update(new Panel($"[blue]累计体力消耗: {EventLogger.vitalSpent}[/]").Expand());
                }

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
                EventLogger.Update(@event);
            }
            // T3 在EventLogger更新后需要开始捕获体力消耗
            if (turn.Turn == 3)
            {
                EventLogger.captureVitalSpending = true;
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

                // 获取温泉挖掘信息（用于预测）
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

                    // KULAN 的挖掘显示（暂时保留）
                    var gain = 0;
                    if (dataset != null && command.TrainIndex > 0 &&
                        dataset.command_info_array.Length > command.TrainLevel - 1)
                    {
                        var dig_array = dataset.command_info_array[command.TrainIndex - 1].dig_info_array;
                        if (dig_array.Length > 0)
                        {
                            gain = dig_array[0].dig_value;
                        }
                    }

                    // 原版的精确挖掘量计算（包含 Link 加成）
                    var calculatedDigAmount = 0;
                    if (remainingLayers > 0)
                    {
                        var supportCardCount = GetSupportCardCount(command);
                        calculatedDigAmount = CalculateDigAmount(supportCardCount, dataset, turn);
                    }

                    // 显示挖掘信息
                    table.AddRow($"Lv{command.TrainLevel} | 挖: {gain}");
                    table.AddRow($"计算值: {calculatedDigAmount}");
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

            // 计算挖掘进度
            var onsen_info = dataset.onsen_info_array.First(x => x.state == 2);
            if (onsen_info != null) {
                var rest = 0;
                foreach (var layer in onsen_info.stratum_info_array)
                {
                    rest += layer.rest_volume;
                }
                layout["挖掘进度"].Update(new Panel($"挖掘进度剩余: {rest}").Expand());
            }

            // 额外信息
            var exTable = new Table().AddColumn("Extras");
            exTable.HideHeaders();
            // 挖掘力
            if (dataset != null && dataset.dig_effect_info_array.Length >= 3)
            {
                exTable.AddRow(new Markup("挖掘力加成"));
                for (var i = 0; i < 3; i++)
                {
                    var value = dataset.dig_effect_info_array[i];
                    string[] toolNames = { "砂", "土", "岩" };
                    exTable.AddRow(new Markup($"{toolNames[i]} Lv {value.item_level} +{value.dig_effect_value}%"));
                }
            }
            // 温泉挖掘完成预测
            var onsen_info_predict = dataset.onsen_info_array.FirstOrDefault(x => x.state == 2);
            if (onsen_info_predict != null)
            {
                // 检查是否已进入 URA 阶段（第 72 回合及以后）
                if (turn.Turn >= 72)
                {
                    // URA 阶段不再显示预测，显示提示信息
                    exTable.AddRow(new Markup("[grey]当前已处在URA阶段，温泉挖掘周期结束[/]"));
                }
                else
                {
                    var predictedTurns = PredictRemainingTurns(dataset, turn) - 1;  // 多了一个回合 减去一下
                    if (predictedTurns > 0)
                    {
                        // 根据预测结果添加颜色标识
                        var predictionMarkup = predictedTurns switch
                        {
                            <= 3 => $"[green]大约剩余{predictedTurns}回合挖完当前温泉[/]",  // 绿色（≤3回合）
                            <= 6 => $"[yellow]大约剩余{predictedTurns}回合挖完当前温泉[/]",  // 黄色（4-6回合）
                            <= 10 => $"[darkorange]大约剩余{predictedTurns}回合挖完当前温泉[/]",  // 橙色（7-10回合）
                            _ => $"[red]大约剩余{predictedTurns}回合挖完当前温泉[/]"  // 红色（>10回合）
                        };
                        exTable.AddRow(new Markup(predictionMarkup));
                    }
                }
            }
            // 体力消耗（测试）
            if (EventLogger.vitalSpent > 0)
            {
                exTable.AddRow(new Markup($"[blue]累计体力消耗: {EventLogger.vitalSpent}[/]"));
            }
            // 计算连续事件表现
            var eventPerf = EventLogger.PrintCardEventPerf(@event.data.chara_info.scenario_id);
            if (eventPerf.Count > 0)
            {
                exTable.AddRow(new Rule());
                foreach (var row in eventPerf)
                    exTable.AddRow(new Markup(row));
            }

            layout["日期"].Update(new Panel($"{turn.Year}{I18N_Year} {turn.Month}{I18N_Month}{turn.HalfMonth}").Expand());
            layout["总属性"].Update(new Panel($"[cyan]总属性: {totalValue}, Pt: {@event.data.chara_info.skill_point}[/]").Expand());
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
            if (@event.data.chara_info.skill_point > 9500)
            {
                critInfos.Add("[red]剩余PT>9500（上限9999），请及时学习技能");
            }
            layout["重要信息"].Update(new Panel(string.Join(Environment.NewLine, critInfos)).Expand());

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
