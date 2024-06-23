using BepInEx;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using UnityEngine;
using GameNetcodeStuff;

namespace MeleeFixes
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.meleefixes", PLUGIN_NAME = "Melee Fixes", PLUGIN_VERSION = "1.2.0";
        internal static new ManualLogSource Logger;

        void Awake()
        {
            Logger = base.Logger;

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    class MeleeFixesPatches
    {
        static readonly MethodInfo GAME_OBJECT_LAYER = AccessTools.DeclaredPropertyGetter(typeof(GameObject), nameof(GameObject.layer));
        static readonly MethodInfo RAYCAST_HIT_TRANSFORM = AccessTools.DeclaredPropertyGetter(typeof(RaycastHit), nameof(RaycastHit.transform));
        static readonly MethodInfo RAYCAST_HIT_COLLIDER = AccessTools.DeclaredPropertyGetter(typeof(RaycastHit), nameof(RaycastHit.collider));
        static readonly MethodInfo COLLIDER_IS_TRIGGER = AccessTools.DeclaredPropertyGetter(typeof(Collider), nameof(Collider.isTrigger));
        //static readonly MethodInfo PHYSICS_LINECAST = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast), new System.Type[]{ typeof(Vector3), typeof(Vector3), typeof(RaycastHit), typeof(int), typeof(QueryTriggerInteraction) });

        static readonly FieldInfo OBJECTS_HIT_BY_SHOVEL_LIST = AccessTools.Field(typeof(Shovel), "objectsHitByShovelList");
        static readonly FieldInfo OBJECTS_HIT_BY_KNIFE_LIST = AccessTools.Field(typeof(KnifeItem), "objectsHitByKnifeList");

        static readonly MethodInfo FIND_OBJECT_OF_TYPE_ROUND_MANAGER = AccessTools.Method(typeof(Object), nameof(Object.FindObjectOfType), null, new System.Type[]{typeof(RoundManager)});
        static readonly MethodInfo ROUND_MANAGER_INSTANCE = AccessTools.DeclaredPropertyGetter(typeof(RoundManager), nameof(RoundManager.Instance));
        static readonly MethodInfo PLAYER_CONTROLLER_B_DAMAGE_ON_OTHER_CLIENTS = AccessTools.Method(typeof(PlayerControllerB), "DamageOnOtherClients");
        static readonly MethodInfo PLAYER_CONTROLLER_B_DAMAGE_PLAYER = AccessTools.Method(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayer));

        static readonly MethodInfo MELEE_HELPER_FILTER_DUPLICATE_HITS = AccessTools.Method(typeof(MeleeHelper), nameof(MeleeHelper.FilterDuplicateHits));
        static readonly MethodInfo MELEE_HELPER_FILTER_INVALID_TARGETS = AccessTools.Method(typeof(MeleeHelper), nameof(MeleeHelper.FilterInvalidTargets));

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.HitShovel)), HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(KnifeItem), nameof(KnifeItem.HitKnife))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransHit(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            bool triggerCheck = false;
            bool filterFunc = false;
            int insertAt = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                //Plugin.Logger.LogDebug(codes[i]);

                // gameObject.layer == 11
                if (!triggerCheck && codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == GAME_OBJECT_LAYER && codes[i + 1].opcode == OpCodes.Ldc_I4_S && (sbyte)codes[i + 1].operand == 11)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (codes[j].opcode == OpCodes.Ldarg_0)
                        {
                            List<CodeInstruction> clone = new();
                            // clone all opcodes up to RaycastHit.transform
                            for (int k = j; codes[k].opcode != OpCodes.Call || (MethodInfo)codes[k].operand != RAYCAST_HIT_TRANSFORM; k++)
                                clone.Add(new CodeInstruction(codes[k].opcode, codes[k].operand));
                            clone.AddRange(new CodeInstruction[]
                            {
                                // RaycastHit.collider
                                new CodeInstruction(OpCodes.Call, RAYCAST_HIT_COLLIDER),
                                // Collider.isTrigger
                                new CodeInstruction(OpCodes.Callvirt, COLLIDER_IS_TRIGGER),
                                // == false
                                new CodeInstruction(OpCodes.Brtrue, codes[i + 2].operand)
                            });

                            for (int k = j - 1; k >= 0; k--)
                            {
                                // need to move label so short-circuit evaluation doesn't skip the trigger check
                                if (codes[k].opcode == OpCodes.Ldc_I4_8)
                                {
                                    Label label = (Label)codes[k + 1].operand;
                                    for (int l = i; l < codes.Count; l++)
                                    {
                                        if (codes[l].labels.Contains(label))
                                        {
                                            codes[l].labels.Remove(label);
                                            clone[0].labels.Add(label);
                                            codes.InsertRange(i + 3, clone);
                                            Plugin.Logger.LogDebug("Transpiler: Add isTrigger check after layer checks");
                                            triggerCheck = true;
                                            break;
                                        }
                                    }
                                }

                                if (triggerCheck)
                                    break;
                            }
                        }

                        if (triggerCheck)
                            break;
                    }
                }
                else if (!filterFunc && codes[i].opcode == OpCodes.Stfld)
                {
                    if ((FieldInfo)codes[i].operand == OBJECTS_HIT_BY_SHOVEL_LIST)
                    {
                        codes.InsertRange(i + 1, new CodeInstruction[]
                        {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldflda, OBJECTS_HIT_BY_SHOVEL_LIST),
                        new CodeInstruction(OpCodes.Call, MELEE_HELPER_FILTER_DUPLICATE_HITS)
                        });
                        Plugin.Logger.LogDebug("Transpiler: Filter duplicate hits from shovel");
                        filterFunc = true;
                    }
                    else if ((FieldInfo)codes[i].operand == OBJECTS_HIT_BY_KNIFE_LIST)
                    {
                        codes.InsertRange(i + 1, new CodeInstruction[]
                        {
                        new CodeInstruction(OpCodes.Ldarg_0),
                        new CodeInstruction(OpCodes.Ldflda, OBJECTS_HIT_BY_KNIFE_LIST),
                        new CodeInstruction(OpCodes.Call, MELEE_HELPER_FILTER_INVALID_TARGETS)
                        });
                        Plugin.Logger.LogDebug("Transpiler: Filter invalid targets from knife");
                        filterFunc = true;
                    }
                }
                else if (codes[i].opcode == OpCodes.Call)
                {
                    MethodInfo methodInfo = (MethodInfo)codes[i].operand;
                    // Physics.Linecast
                    if (insertAt == -1 && methodInfo.Name.Equals(nameof(Physics.Linecast)))
                        insertAt = i;
                    // FindObjectOfType<RoundManager>()
                    else if (methodInfo == FIND_OBJECT_OF_TYPE_ROUND_MANAGER)
                    {
                        codes[i].operand = ROUND_MANAGER_INSTANCE;
                        Plugin.Logger.LogDebug("Transpiler: Replace FindObjectOfType<RoundManager>() with RoundManager.Instance");
                    }
                }
            }

            if (insertAt > -1)
            {
                codes[insertAt].operand = typeof(Physics).GetMethods().First(method => method.Name.Equals(nameof(Physics.Linecast)) && method.GetParameters().Length == 5); //PHYSICS_LINECAST
                codes.Insert(insertAt, new CodeInstruction(OpCodes.Ldc_I4_1));
                Plugin.Logger.LogDebug("Transpiler: Add QueryTriggerInteraction.Ignore to Physics.Linecast");
            }

            return codes;
        }

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.ItemActivate))]
        [HarmonyPrefix]
        static void ShovelPreItemActivate(Shovel __instance, PlayerControllerB ___previousPlayerHeldBy, ref bool ___reelingUp)
        {
            if (___reelingUp && ___previousPlayerHeldBy != __instance.playerHeldBy)
            {
                ___reelingUp = false;
                Plugin.Logger.LogInfo("Reset broken shovel to allow swinging it again");
            }
        }

        [HarmonyPatch(typeof(PlayerControllerB), nameof(PlayerControllerB.DamagePlayerFromOtherClientClientRpc))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransDamagePlayerFromOtherClientClientRpc(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            for (int i = 3; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Call)
                {
                    MethodInfo methodInfo = (MethodInfo)codes[i].operand;
                    if (methodInfo == PLAYER_CONTROLLER_B_DAMAGE_ON_OTHER_CLIENTS)
                    {
                        // don't call DamageOnOtherClients (instead, let the damage function call the RPC after calculating health)
                        for (int j = i - 3; j <= i; j++)
                            codes[j].opcode = OpCodes.Nop;
                        Plugin.Logger.LogDebug("Transpiler: Fix duplicated damage call on players");
                    }
                    else if (methodInfo == PLAYER_CONTROLLER_B_DAMAGE_PLAYER)
                    {
                        for (int j = i - 1; j > 0; j--)
                        {
                            if (codes[j].opcode == OpCodes.Ldarg_1)
                            {
                                codes[j + 1].opcode = OpCodes.Ldc_I4_0; // hasDamageSFX: false
                                codes[j + 2].opcode = OpCodes.Ldc_I4_1; // callRPC: true
                                Plugin.Logger.LogDebug("Transpiler: Transmit damage RPC *after* friendly fire");
                                break;
                            }
                        }
                    }
                }
            }

            return codes;
        }
    }

    static class MeleeHelper
    {
        internal static void FilterDuplicateHits(ref List<RaycastHit> hits)
        {
            HashSet<IHittable> uniqueTargets = new();
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].transform.TryGetComponent(out IHittable hittable))
                {
                    if (!uniqueTargets.Add(hittable))
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered duplicate shovel hit on \"{hits[i].transform.name}\"");
                    }
                }
            }
        }

        internal static void FilterInvalidTargets(ref List<RaycastHit> hits)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].transform.TryGetComponent(out IHittable hittable))
                {
                    EnemyAICollisionDetect enemyAICollisionDetect = hittable as EnemyAICollisionDetect;
                    if (enemyAICollisionDetect != null && (enemyAICollisionDetect.onlyCollideWhenGrounded || enemyAICollisionDetect.mainScript.isEnemyDead))
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered invalid knife hit on \"{hits[i].transform.name}\"");
                    }
                }
            }
        }
    }
}