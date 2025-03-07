﻿/**
 * This file is written and licensed by Paulov (https://github.com/paulov-t) for Stay in Tarkov (https://github.com/stayintarkov)
 * You are not allowed to reproduce this file in any other project
 */

using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.UI;
using EFT.Weather;
using JsonType;
using System;
using System.Collections.Generic;

namespace StayInTarkov.Coop.SITGameModes
{
    public abstract class AbstractSITGame : BaseLocalGame<GamePlayerOwner>, IBotGame
    {
        public new bool InRaid { get { return true; } }

        public ISession BackEndSession { get { return StayInTarkovHelperConstants.BackEndSession; } }

        BotsController IBotGame.BotsController
        {
            get
            {
                if (BotsController == null)
                {
                    BotsController = (BotsController)ReflectionHelpers.GetFieldFromTypeByFieldType(GetType(), typeof(BotsController)).GetValue(this);
                }
                return BotsController;
            }
        }

        private static BotsController BotsController;

        public BotsController PBotsController
        {
            get
            {
                if (BotsController == null)
                {
                    BotsController = (BotsController)ReflectionHelpers.GetFieldFromTypeByFieldType(GetType(), typeof(BotsController)).GetValue(this);
                }
                return BotsController;
            }
        }

        public IWeatherCurve WeatherCurve
        {
            get
            {
                if (WeatherController.Instance != null)
                    return new WeatherCurve(new WeatherClass[1] { new() });

                return null;
            }
        }

        ManualLogSource Logger { get; set; }

        // Token: 0x0600844F RID: 33871 RVA: 0x0025D580 File Offset: 0x0025B780
        public static T Create<T>(
            InputTree inputTree
            , Profile profile
            , GameDateTime backendDateTime
            , InsuranceCompanyClass insurance
            , MenuUI menuUI
            , CommonUI commonUI
            , PreloaderUI preloaderUI
            , GameUI gameUI
            , LocationSettingsClass.Location location
            , TimeAndWeatherSettings timeAndWeather
            , WavesSettings wavesSettings
            , EDateTime dateTime
            , Callback<ExitStatus, TimeSpan, MetricsClass> callback
            , float fixedDeltaTime
            , EUpdateQueue updateQueue
            , ISession backEndSession
            , TimeSpan sessionTime) where T : AbstractSITGame
        {

            var r =
               smethod_0<T>(inputTree, profile, backendDateTime, insurance, menuUI, commonUI, preloaderUI, gameUI, location, timeAndWeather, wavesSettings, dateTime
               , callback, fixedDeltaTime, updateQueue, backEndSession, new TimeSpan?(sessionTime));

            r.Logger = BepInEx.Logging.Logger.CreateLogSource("Coop Game Mode");
            r.Logger.LogInfo("CoopGame.Create");

            // Non Waves Scenario setup
            r.nonWavesSpawnScenario_0 = (NonWavesSpawnScenario)ReflectionHelpers.GetMethodForType(typeof(NonWavesSpawnScenario), "smethod_0").Invoke
                (null, new object[] { r, location, r.PBotsController });
            r.nonWavesSpawnScenario_0.ImplementWaveSettings(wavesSettings);

            // Waves Scenario setup
            r.wavesSpawnScenario_0 = (WavesSpawnScenario)ReflectionHelpers.GetMethodForType(typeof(WavesSpawnScenario), "smethod_0").Invoke
                (null, new object[] {
                    r.gameObject
                    , location.waves
                    , new Action<BotSpawnWave>((wave) => r.PBotsController.ActivateBotsByWave(wave))
                    , location });

            var bosswavemanagerValue = ReflectionHelpers.GetMethodForType(typeof(BossWaveManager), "smethod_0").Invoke
                (null, new object[] { location.BossLocationSpawn, new Action<BossLocationSpawn>((bossWave) => { r.PBotsController.ActivateBotsByWave(bossWave); }) });
            ReflectionHelpers.GetFieldFromTypeByFieldType(r.GetType(), typeof(BossWaveManager)).SetValue(r, bosswavemanagerValue);
            r.BossWaveManager = bosswavemanagerValue as BossWaveManager;

            r.func_1 = (player) => GamePlayerOwner.Create<GamePlayerOwner>(player, inputTree, insurance, backEndSession, commonUI, preloaderUI, gameUI, r.GameDateTime, location);

            return r;
        }

        public Dictionary<string, EFT.Player> Bots { get; set; } = new Dictionary<string, EFT.Player>();

        /// <summary>
        /// Matchmaker countdown
        /// </summary>
        /// <param name="timeBeforeDeploy"></param>
        public override void vmethod_1(float timeBeforeDeploy)
        {
            base.vmethod_1(timeBeforeDeploy);
        }

        /// <summary>
        /// Reconnection handling.
        /// </summary>
        public override void vmethod_3()
        {
            base.vmethod_3();
        }

        private BossWaveManager BossWaveManager;

        private WavesSpawnScenario wavesSpawnScenario_0;

        private NonWavesSpawnScenario nonWavesSpawnScenario_0;

        private Func<EFT.Player, GamePlayerOwner> func_1;


        public new void method_6(string backendUrl, string locationId, int variantId)
        {
            Logger.LogInfo("CoopGame:method_6");
            return;
        }
    }
}
