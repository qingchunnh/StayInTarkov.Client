﻿using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.HealthSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using StayInTarkov.Coop.Components.CoopGameComponents;
using StayInTarkov.Coop.Controllers;
using StayInTarkov.Coop.Controllers.CoopInventory;
using StayInTarkov.Coop.Controllers.HandControllers;
using StayInTarkov.Coop.Controllers.Health;
using StayInTarkov.Coop.NetworkPacket.Player;
using StayInTarkov.Coop.NetworkPacket.Player.Health;
using StayInTarkov.Coop.NetworkPacket.Player.Proceed;
using StayInTarkov.Core.Player;
using StayInTarkov.Networking;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

namespace StayInTarkov.Coop.Players
{
    public class CoopPlayer : LocalPlayer
    {
        public virtual ManualLogSource BepInLogger { get; } = BepInEx.Logging.Logger.CreateLogSource("CoopPlayer");

        public static async Task<LocalPlayer>
            Create(int playerId
            , Vector3 position
            , Quaternion rotation
            , string layerName
            , string prefix
            , EPointOfView pointOfView
            , Profile profile
            , bool aiControl
            , EUpdateQueue updateQueue
            , EUpdateMode armsUpdateMode
            , EUpdateMode bodyUpdateMode
            , CharacterControllerSpawner.Mode characterControllerMode
            , Func<float> getSensitivity, Func<float> getAimingSensitivity
            , IFilterCustomization filter
            , AbstractQuestControllerClass questController = null
            , AbstractAchievementControllerClass achievementsController = null
            , bool isYourPlayer = false
            , bool isClientDrone = false)
        {
            CoopPlayer player = null;

            if (isClientDrone)
            {
                player = EFT.Player.Create<CoopPlayerClient>(
                    ResourceBundleConstants.PLAYER_BUNDLE_NAME
                    , playerId
                    , position
                    , updateQueue
                    , armsUpdateMode
                    , bodyUpdateMode
                    , characterControllerMode
                    , getSensitivity
                    , getAimingSensitivity
                    , prefix
                    , aiControl);
                player.name = profile.Nickname;
            }
            else
            {
                player = Create<CoopPlayer>(
                    ResourceBundleConstants.PLAYER_BUNDLE_NAME
                    , playerId
                    , position
                    , updateQueue
                    , armsUpdateMode
                    , bodyUpdateMode
                    , characterControllerMode
                    , getSensitivity
                    , getAimingSensitivity
                    , prefix
                    , aiControl);
               
            }
            player.IsYourPlayer = isYourPlayer;
            player.Position = position;

            InventoryControllerClass inventoryController = isYourPlayer || player is CoopPlayer
                ? new CoopInventoryController(player, profile, false)
                : new CoopInventoryControllerClient(player, profile, false);

            foreach(var item in profile.Inventory.AllRealPlayerItems)
            {
                if(item.Owner == null)
                {
                    player.BepInLogger.LogInfo("Owner is null. wtf");
                }
            }

            // Quest Controller instantiate
            if (isYourPlayer)
            {
                questController = PlayerFactory.GetQuestController(profile, inventoryController);
                player.BepInLogger.LogDebug($"{nameof(questController)} Instantiated");
            }

            // Achievement Controller instantiate
            if (isYourPlayer)
            {
                achievementsController = PlayerFactory.GetAchievementController(profile, inventoryController);
                player.BepInLogger.LogDebug($"{nameof(achievementsController)} Instantiated");
            }

            IStatisticsManager statsManager = isYourPlayer ? PlayerFactory.GetStatisticsManager(player) : new NullStatisticsManager();
            player.BepInLogger.LogDebug($"{nameof(statsManager)} Instantiated with type {statsManager.GetType()}");

            // TODO: Convert over to own Health Controller. For some reason, something is hard coded to use PlayerHealthController to discard/use items when depleted. I have to use PlayerHealthController for now to fix.
            //IHealthController healthController = isClientDrone
            //? new PlayerHealthController(profile.Health, player, inventoryController, profile.Skills, true)//   new CoopHealthControllerClient(profile.Health, player, inventoryController, profile.Skills, isClientDrone ? false : aiControl)
            //: new CoopHealthController(profile.Health, player, inventoryController, profile.Skills, isClientDrone ? false : aiControl);
            IHealthController healthController = 
                // aiControl = true is VITAL, otherwise items will not be used!
                // found the fault is caused by aiControl allows ManualUpdate to be used
                //isClientDrone ? new PlayerHealthController(profile.Health, player, inventoryController, profile.Skills, true) 
                isClientDrone ? new SITHealthControllerClient(profile.Health, player, inventoryController, profile.Skills)  // new PlayerHealthController(profile.Health, player, inventoryController, profile.Skills, true) 
                : new SITHealthController(profile.Health, player, inventoryController, profile.Skills, aiControl);

            player.BepInLogger.LogDebug($"{nameof(healthController)} Instantiated with type {healthController.GetType()}");

            await player
                .Init(rotation, layerName, pointOfView, profile, inventoryController
                , healthController
                , statsManager
                , questController
                , achievementsController
                , filter
                , aiControl || isClientDrone ? EVoipState.NotAvailable : EVoipState.Available
                , aiControl
                , async: false);

            player._handsController = EmptyHandsController.smethod_5<EmptyHandsController>(player);
            player._handsController.Spawn(1f, () => { });
            player.AIData = new AIData(null, player);
            player.AggressorFound = false;
            player._animators[0].enabled = true;
            if (!player.IsYourPlayer)
                player._armsUpdateQueue = EUpdateQueue.Update;
            player.Position = position;

            // If this is a Client Drone add Player Replicated Component
            if (isClientDrone)
            {
                var prc = player.GetOrAddComponent<PlayerReplicatedComponent>();
                prc.IsClientDrone = true;
            }

            return player;
        }

        public override ApplyShot ApplyShot(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, EArmorPlateCollider armorPlateCollider, ShotId shotId)
        {
            if (!CoopGameComponent.TryGetCoopGameComponent(out var coopGameComponent))
                return null;

            if (!coopGameComponent.GameWorldGameStarted)
                return null;

            return base.ApplyShot(damageInfo, bodyPartType, colliderType, armorPlateCollider, shotId);
        }

        public override void ApplyDamageInfo(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType colliderType, float absorbed)
        {
            if (!CoopGameComponent.TryGetCoopGameComponent(out var coopGameComponent))
                return;

            if (!coopGameComponent.GameWorldGameStarted)
                return;

            SendDamageToAllClients(damageInfo, bodyPartType, colliderType, absorbed);
        }

        /// <summary>
        /// Paulov: TODO/FIXME: This is an expensive memory leaking operation that runs on both Server & Client. Needs a rewrite.
        /// </summary>
        /// <param name="damageInfo"></param>
        /// <param name="bodyPartType"></param>
        /// <param name="bodyPartColliderType"></param>
        /// <param name="absorbed"></param>
        private void SendDamageToAllClients(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType bodyPartColliderType, float absorbed)
        {
            ApplyDamagePacket damagePacket = new ApplyDamagePacket();
            damagePacket.ProfileId = this.ProfileId;
            damagePacket.Damage = damageInfo.Damage;
            damagePacket.DamageType = damageInfo.DamageType;
            damagePacket.BodyPart = bodyPartType;
            damagePacket.ColliderType = bodyPartColliderType;
            damagePacket.Absorbed = absorbed;
            if (damageInfo.Player != null)
            {
                damagePacket.AggressorProfileId = damageInfo.Player.iPlayer.ProfileId;
                if (damageInfo.Weapon != null)
                {
                    damagePacket.AggressorWeaponId = damageInfo.Weapon.Id;
                    damagePacket.AggressorWeaponTpl = damageInfo.Weapon.TemplateId;
                }
            }
            GameClient.SendData(damagePacket.Serialize());

            //Dictionary<string, object> packet = new();
            //damageInfo.HitCollider = null;
            //damageInfo.HittedBallisticCollider = null;
            //Dictionary<string, string> playerDict = new();
            //if (damageInfo.Player != null)
            //{
            //    playerDict.Add("d.p.aid", damageInfo.Player.iPlayer.Profile.AccountId);
            //    playerDict.Add("d.p.id", damageInfo.Player.iPlayer.ProfileId);
            //}

            //damageInfo.Player = null;
            //Dictionary<string, string> weaponDict = new();

            //if (damageInfo.Weapon != null)
            //{
            //    packet.Add("d.w.tpl", damageInfo.Weapon.TemplateId);
            //    packet.Add("d.w.id", damageInfo.Weapon.Id);
            //}
            //damageInfo.Weapon = null;

            //packet.Add("d", damageInfo.SITToJson());
            //packet.Add("d.p", playerDict);
            //packet.Add("d.w", weaponDict);
            //packet.Add("bpt", bodyPartType.ToString());
            //packet.Add("bpct", bodyPartColliderType.ToString());
            //packet.Add("ab", absorbed.ToString());
            //packet.Add("m", "ApplyDamageInfo");

            //AkiBackendCommunicationCoop.PostLocalPlayerData(this, packet);
        }

        public void ReceiveDamageFromServer(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType bodyPartColliderType, float absorbed)
        {
            StartCoroutine(ReceiveDamageFromServerCR(damageInfo, bodyPartType, bodyPartColliderType, absorbed));
        }

        public IEnumerator ReceiveDamageFromServerCR(DamageInfo damageInfo, EBodyPart bodyPartType, EBodyPartColliderType bodyPartColliderType, float absorbed)
        {
            if (damageInfo.DamageType == EDamageType.Bullet && IsYourPlayer)
            {
                float handsShake = 0.05f;
                float cameraShake = 0.4f;
                float absorbedDamage = absorbed + damageInfo.Damage;

                switch (bodyPartType)
                {
                    case EBodyPart.Head:
                        handsShake = 0.1f;
                        cameraShake = 1.3f;
                        break;
                    case EBodyPart.LeftArm:
                    case EBodyPart.RightArm:
                        handsShake = 0.15f;
                        cameraShake = 0.5f;
                        break;
                    case EBodyPart.LeftLeg:
                    case EBodyPart.RightLeg:
                        cameraShake = 0.3f;
                        break;
                }

                ProceduralWeaponAnimation.ForceReact.AddForce(Mathf.Sqrt(absorbedDamage) / 10, handsShake, cameraShake);
                if (FPSCamera.Instance.EffectsController.TryGetComponent(out FastBlur fastBlur))
                {
                    fastBlur.enabled = true;
                    fastBlur.Hit(MovementContext.PhysicalConditionIs(EPhysicalCondition.OnPainkillers) ? absorbedDamage : bodyPartType == EBodyPart.Head ? absorbedDamage * 6 : absorbedDamage * 3);
                }
            }

            BepInLogger.LogDebug($"{nameof(ApplyDamageInfo)}:{ProfileId}:{damageInfo.DamageType}:{damageInfo.Damage}");
            base.ApplyDamageInfo(damageInfo, bodyPartType, bodyPartColliderType, absorbed);

            yield break;

        }

        //public override PlayerHitInfo ApplyShot(DamageInfo damageInfo, EBodyPart bodyPartType, ShotId shotId)
        //{
        //    return base.ApplyShot(damageInfo, bodyPartType, shotId);
        //}

        //public void ReceiveApplyShotFromServer(Dictionary<string, object> dict)
        //{
        //    Logger.LogDebug("ReceiveApplyShotFromServer");
        //    Enum.TryParse<EBodyPart>(dict["bpt"].ToString(), out var bodyPartType);
        //    Enum.TryParse<EHeadSegment>(dict["hs"].ToString(), out var headSegment);
        //    var absorbed = float.Parse(dict["ab"].ToString());

        //    var damageInfo = Player_ApplyShot_Patch.BuildDamageInfoFromPacket(dict);
        //    damageInfo.HitCollider = Player_ApplyShot_Patch.GetCollider(this, damageInfo.BodyPartColliderType);

        //    var shotId = new ShotId();
        //    if (dict.ContainsKey("ammoid") && dict["ammoid"] != null)
        //    {
        //        shotId = new ShotId(dict["ammoid"].ToString(), 1);
        //    }

        //    base.ApplyShot(damageInfo, bodyPartType, shotId);
        //}

        public override Corpse CreateCorpse()
        {
            return base.CreateCorpse();
        }

        public override void OnItemAddedOrRemoved(Item item, ItemAddress location, bool added)
        {
            base.OnItemAddedOrRemoved(item, location, added);
        }


        public override void Rotate(Vector2 deltaRotation, bool ignoreClamp = false)
        {
            if (
                TriggerPressed
                && LastRotationSent != Rotation
                )
            {
                PlayerRotatePacket packet = new PlayerRotatePacket(this.ProfileId);
                packet.RotationX = Rotation.x;
                packet.RotationY = Rotation.y;
                GameClient.SendData(packet.Serialize());
                LastRotationSent = Rotation;
            }

            base.Rotate(deltaRotation, ignoreClamp);
        }

        #region Speaking

        public override void Say(EPhraseTrigger @event, bool demand = false, float delay = 0, ETagStatus mask = 0, int probability = 100, bool aggressive = false)
        {
            base.Say(@event, demand, delay, mask, probability, aggressive);

            if (this is CoopPlayerClient)
                return;

            if (@event == EPhraseTrigger.Cooperation)
            {
                //vmethod_3(EGesture.Hello);
            }
            if (@event == EPhraseTrigger.MumblePhrase)
            {
                @event = ((aggressive || Time.time < Awareness) ? EPhraseTrigger.OnFight : EPhraseTrigger.OnMutter);
            }

            //PlayerSayPacket sayPacket = new PlayerSayPacket();
            //sayPacket.ProfileId = this.ProfileId;
            //sayPacket.Trigger = @event;
            //sayPacket.Delay = delay;
            //sayPacket.Mask = mask;
            //sayPacket.Aggressive = aggressive;
            ////sayPacket.Index = clip.NetId;
            //GameClient.SendData(sayPacket.Serialize());
        }

        public override void OnPhraseTold(EPhraseTrigger @event, TaggedClip clip, TagBank bank, Speaker speaker)
        {
            base.OnPhraseTold(@event, clip, bank, speaker);

            // If a client. Do not send a packet.
            if (this is CoopPlayerClient)
                return;

            PlayerSayPacket sayPacket = new PlayerSayPacket();
            sayPacket.ProfileId = this.ProfileId;
            sayPacket.Trigger = @event;
            sayPacket.Index = clip.NetId;
            GameClient.SendData(sayPacket.Serialize());
        }

        public virtual void ReceiveSay(EPhraseTrigger trigger, int index, ETagStatus mask, bool aggressive)
        {
            //BepInLogger.LogDebug($"{nameof(ReceiveSay)}({trigger},{mask})");

            //if (this is CoopPlayer)
            //    return;

            ////Speaker.PlayDirect(trigger, index);

            //ETagStatus eTagStatus = ((!aggressive && !(Awareness > Time.time)) ? ETagStatus.Unaware : ETagStatus.Combat);
            //Speaker.Play(trigger, HealthStatus | mask | eTagStatus, true, 100);
        }

        #endregion

        public override void OnDestroy()
        {
            BepInLogger.LogDebug("OnDestroy()");

            base.OnDestroy();
        }

        public virtual void ReceivePlayerStatePacket(PlayerStatePacket playerStatePacket)
        {
           
        }

        protected virtual void Interpolate()
        {

        }

        public override void UpdateTick()
        {
            base.UpdateTick();
        }

        public override void OnDead(EDamageType damageType)
        {
            base.OnDead(damageType);
            EFT.Player victim = this;

            var attacker = LastAggressor as EFT.Player;

            // Paulov: Unknown / Unable to replicate issue where some User's feed would cause a crash
            //if(PluginConfigSettings.Instance.CoopSettings.SETTING_ShowFeed)
            //    DisplayMessageNotifications.DisplayMessageNotification(attacker != null ? $"\"{GeneratePlayerNameWithSide(attacker)}\" killed \"{GeneratePlayerNameWithSide(victim)}\"" : $"\"{GeneratePlayerNameWithSide(victim)}\" has died because of \"{("DamageType_" + damageType.ToString()).Localized()}\"");
            
            using KillPacket killPacket = new KillPacket(ProfileId, damageType);
            GameClient.SendData(killPacket.Serialize());
        }

        public static string GeneratePlayerNameWithSide(EFT.Player player)
        {
            if (player == null)
                return "";

            var side = "Scav";

            if (player.AIData.IAmBoss)
                side = "Boss";
            else if (player.Side != EPlayerSide.Savage)
                side = player.Side.ToString();

            return $"[{side}] {player.Profile.Nickname}";
        }



        protected struct SITPostProceedData
        {
            public Item UsedItem { get; set; }

            public IHandsController HandsController { get; set; }

            public float? PreviousAmount { get; set; }
            public float? NewValue { get; set; }

            public override string ToString()
            {
                return $"{UsedItem}:{PreviousAmount}:{NewValue}";
            }
        } 

        protected SITPostProceedData? PostProceedData { get; set; }
        public bool TriggerPressed { get; internal set; }

        private Vector2 LastRotationSent = Vector2.zero;

        public override void Proceed(FoodClass foodDrink, float amount, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
        {
            // Protection
            if (this is CoopPlayerClient)
            {
                base.Proceed(foodDrink, amount, callback, animationVariant, scheduled);
                return;
            }

            var startResource = foodDrink.FoodDrinkComponent.RelativeValue;
            PostProceedData = new SITPostProceedData { PreviousAmount = startResource, UsedItem = foodDrink };

            base.Proceed(foodDrink, amount, callback, animationVariant, scheduled);

            // Extra unneccessary protection
            if (this is CoopPlayer)
            {
                PlayerProceedFoodDrinkPacket foodDrinkPacket = new PlayerProceedFoodDrinkPacket(this.ProfileId, foodDrink.Id, foodDrink.TemplateId, amount, animationVariant, scheduled);
                BepInLogger.LogDebug(foodDrinkPacket.ToJson());
                GameClient.SendData(foodDrinkPacket.Serialize());
            }
        }

        public override void Proceed(MedsClass meds, EBodyPart bodyPart, Callback<IMedsController> callback, int animationVariant, bool scheduled = true)
        {
            // Protection
            if (this is CoopPlayerClient)
            {
                base.Proceed(meds, bodyPart, callback, animationVariant, scheduled);
                return;
            }

            var startResource = meds != null && meds.MedKitComponent != null ? meds.MedKitComponent.HpResource : 1;

            BepInLogger.LogDebug($"{nameof(CoopPlayer)}:{nameof(Proceed)}:{nameof(meds)}:{bodyPart}");
            Func<MedsController> controllerFactory = () => MedsController.smethod_5<MedsController>(this, meds, bodyPart, 1f, animationVariant);
            new Process<MedsController, IMedsController>(this, controllerFactory, meds).method_0(null, (x) => {
                PostProceedData = new SITPostProceedData { PreviousAmount = startResource, UsedItem = meds, HandsController = x.Value };
                callback(x);
            },false);

            // Extra unneccessary protection
            if (this is CoopPlayer)
            {
                PlayerProceedMedsPacket medsPacket = new PlayerProceedMedsPacket(this.ProfileId, meds.Id, meds.TemplateId, bodyPart, animationVariant, scheduled, 1f);
                GameClient.SendData(medsPacket.Serialize());
            }

        }

        public override void Proceed(GrenadeClass throwWeap, Callback<IGrenadeQuickUseController> callback, bool scheduled = true)
        {
            BepInLogger.LogDebug($"{nameof(CoopPlayer)}:{nameof(Proceed)}:{nameof(throwWeap)}:IGrenadeQuickUseController");
            base.Proceed(throwWeap, callback, scheduled);
        }

        public override void Proceed(GrenadeClass throwWeap, Callback<IThrowableCallback> callback, bool scheduled = true)
        {
            BepInLogger.LogDebug($"{nameof(CoopPlayer)}:{nameof(Proceed)}:{nameof(throwWeap)}:IThrowableCallback");
            //base.Proceed(throwWeap, callback, scheduled);

            Func<SITGrenadeController> controllerFactory = () => GrenadeController.smethod_8<SITGrenadeController>(this, throwWeap);
            new Process<SITGrenadeController, IThrowableCallback>(this, controllerFactory, throwWeap).method_0(null, callback, scheduled);

            PlayerProceedGrenadePacket packet = new PlayerProceedGrenadePacket(ProfileId, throwWeap.Id, scheduled);
            GameClient.SendData(packet.Serialize());    
        }

        public override void Proceed(Item item, Callback<IQuickUseController> callback, bool scheduled = true)
        {
            BepInLogger.LogDebug($"{nameof(CoopPlayer)}:{nameof(Proceed)}:{nameof(item)}:IQuickUseController");
            base.Proceed(item, callback, scheduled);


        }

        public override void Proceed(Weapon weapon, Callback<IFirearmHandsController> callback, bool scheduled = true)
        {
            Func<FirearmController> controllerFactory = ((!IsAI) ? ((Func<FirearmController>)(() => FirearmController.smethod_5<SITFirearmController>(this, weapon))) : ((Func<FirearmController>)(() => FirearmController.smethod_5<SITFirearmControllerAI>(this, weapon))));
            bool fastHide = false;
            if (_handsController is FirearmController firearmController)
            {
                fastHide = firearmController.CheckForFastWeaponSwitch(weapon);
            }
            new Process<FirearmController, IFirearmHandsController>(this, controllerFactory, weapon, fastHide).method_0(null, callback, scheduled);
            
            PlayerProceedWeaponPacket weaponPacket = new PlayerProceedWeaponPacket();
            weaponPacket.ProfileId = this.ProfileId;
            weaponPacket.ItemId = weapon.Id;
            weaponPacket.Scheduled = scheduled;
            GameClient.SendData(weaponPacket.Serialize());
        }

        public override void Proceed(KnifeComponent knife, Callback<IKnifeController> callback, bool scheduled = true)
        {
            Func<SITKnifeController> controllerFactory = () => KnifeController.smethod_8<SITKnifeController>(this, knife);
            new Process<SITKnifeController, IKnifeController>(this, controllerFactory, knife.Item, fastHide: true)
                .method_0((IResult result) => {

                    // Check if the Proceed was successful before sending packet
                    if (result.Succeed)
                    {
                        PlayerProceedKnifePacket knifePacket = new PlayerProceedKnifePacket();
                        knifePacket.ProfileId = this.ProfileId;
                        knifePacket.ItemId = knife.Item.Id;
                        knifePacket.Scheduled = scheduled;
                        GameClient.SendData(knifePacket.Serialize());
                    }

                }, callback, scheduled);

            
        }

        public override void Proceed(KnifeComponent knife, Callback<IQuickKnifeKickController> callback, bool scheduled = true)
        {
            Func<SITQuickKnifeKickController> controllerFactory = () => QuickKnifeKickController.smethod_8<SITQuickKnifeKickController>(this, knife);
            var process = new Process<SITQuickKnifeKickController, IQuickKnifeKickController>(this, controllerFactory, knife.Item, fastHide: true, AbstractProcess.Completion.Sync, AbstractProcess.Confirmation.Succeed, skippable: false);
            process.method_0(delegate (IResult result)
            {
                // Check if the Proceed was successful before sending packet
                if (result.Succeed)
                {
                    PlayerProceedKnifePacket knifePacket = new PlayerProceedKnifePacket();
                    knifePacket.ProfileId = this.ProfileId;
                    knifePacket.ItemId = knife.Item.Id;
                    knifePacket.Scheduled = scheduled;
                    knifePacket.QuickKnife = true;
                    GameClient.SendData(knifePacket.Serialize());
                }

            }, callback, scheduled);

            
        }


        public override void DropCurrentController(Action callback, bool fastDrop, Item nextControllerItem = null)
        {
            BepInLogger.LogDebug($"{nameof(CoopPlayer)}:{nameof(DropCurrentController)}");

            // Handle inventory item syncronization AFTER a proceed operation has occurred. 
            // This will ensure sync of all items after use. No matter what the Clients did on their end.
            if(PostProceedData.HasValue)
            {
                var newValue = PostProceedData.Value.NewValue.HasValue ? PostProceedData.Value.NewValue.Value : 0f;
                if (!PostProceedData.Value.NewValue.HasValue && PostProceedData.Value.PreviousAmount.HasValue)
                {
                    if (PostProceedData.Value.UsedItem is MedsClass meds)
                    {
                        newValue = meds != null && meds.MedKitComponent != null ? meds.MedKitComponent.HpResource : 0;
                    }
                    if (PostProceedData.Value.UsedItem is FoodClass food)
                    {
                        newValue = food != null && food.FoodDrinkComponent != null ? food.FoodDrinkComponent.HpPercent : 0;
                    }
                }

                BepInLogger.LogDebug($"{PostProceedData.Value}");
                PlayerPostProceedDataSyncPacket postProceedPacket = new PlayerPostProceedDataSyncPacket(this.ProfileId, PostProceedData.Value.UsedItem.Id, newValue, PostProceedData.Value.UsedItem.StackObjectsCount);
                GameClient.SendData(postProceedPacket.Serialize());

                PostProceedData = null;
            }

            base.DropCurrentController(callback, fastDrop, nextControllerItem);
        }

        void Awake()
        {
        }

        public override void ComplexLateUpdate(EUpdateQueue queue, float deltaTime)
        {
            try
            {
                base.ComplexLateUpdate(queue, deltaTime);
            }
            catch (Exception ex)
            {
                BepInLogger.LogError(ex);
            }
        }


    }
}
