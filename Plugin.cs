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
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.meleefixes", PLUGIN_NAME = "Melee Fixes", PLUGIN_VERSION = "1.3.0";
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

        static readonly FieldInfo OBJECTS_HIT_BY_SHOVEL_LIST = AccessTools.Field(typeof(Shovel), "objectsHitByShovelList");
        static readonly FieldInfo OBJECTS_HIT_BY_KNIFE_LIST = AccessTools.Field(typeof(KnifeItem), "objectsHitByKnifeList");

        static readonly MethodInfo FIND_OBJECT_OF_TYPE_ROUND_MANAGER = AccessTools.Method(typeof(Object), nameof(Object.FindObjectOfType), null, [typeof(RoundManager)]);
        static readonly MethodInfo ROUND_MANAGER_INSTANCE = AccessTools.DeclaredPropertyGetter(typeof(RoundManager), nameof(RoundManager.Instance));

        static readonly MethodInfo MELEE_HELPER_FILTER_TARGETS = AccessTools.Method(typeof(MeleeHelper), nameof(MeleeHelper.FilterTargets));

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.HitShovel)), HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(KnifeItem), nameof(KnifeItem.HitKnife))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> TransHit(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            bool triggerCheck = true; // will be set to false if patching the knife
            bool filterFunc = false;
            for (int i = 0; i < codes.Count; i++)
            {
                //Plugin.Logger.LogDebug(codes[i]);

                if (!filterFunc && codes[i].opcode == OpCodes.Stfld)
                {
                    FieldInfo fieldInfo = (FieldInfo)codes[i].operand;
                    if (fieldInfo == OBJECTS_HIT_BY_SHOVEL_LIST || fieldInfo == OBJECTS_HIT_BY_KNIFE_LIST)
                    {
                        codes.InsertRange(i + 1, [
                            new CodeInstruction(OpCodes.Ldarg_0),
                            new CodeInstruction(OpCodes.Ldflda, codes[i].operand),
                            new CodeInstruction(OpCodes.Call, MELEE_HELPER_FILTER_TARGETS)
                        ]);
                        Plugin.Logger.LogDebug("Transpiler: Filter targets by validity");
                        filterFunc = true;

                        // trigger check is only necessary for the knife
                        if (fieldInfo == OBJECTS_HIT_BY_KNIFE_LIST)
                            triggerCheck = false;
                    }
                }
                // gameObject.layer == 11
                else if (!triggerCheck && codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == GAME_OBJECT_LAYER && codes[i + 1].opcode == OpCodes.Ldc_I4_S && (sbyte)codes[i + 1].operand == 11)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (codes[j].opcode == OpCodes.Ldarg_0)
                        {
                            List<CodeInstruction> clone = [];
                            // clone all opcodes up to RaycastHit.transform
                            for (int k = j; codes[k].opcode != OpCodes.Call || (MethodInfo)codes[k].operand != RAYCAST_HIT_TRANSFORM; k++)
                                clone.Add(new CodeInstruction(codes[k].opcode, codes[k].operand));
                            clone.AddRange([
                                // RaycastHit.collider
                                new CodeInstruction(OpCodes.Call, RAYCAST_HIT_COLLIDER),
                                // Collider.isTrigger
                                new CodeInstruction(OpCodes.Callvirt, COLLIDER_IS_TRIGGER),
                                // == false
                                new CodeInstruction(OpCodes.Brtrue, codes[i + 2].operand)
                            ]);

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
                                            Plugin.Logger.LogDebug("Transpiler (Knife): Add isTrigger check after layer checks");
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
                else if (codes[i].opcode == OpCodes.Call && (MethodInfo)codes[i].operand == FIND_OBJECT_OF_TYPE_ROUND_MANAGER)
                {
                    codes[i].operand = ROUND_MANAGER_INSTANCE;
                    Plugin.Logger.LogDebug("Transpiler: Replace FindObjectOfType<RoundManager>() with RoundManager.Instance");
                }
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
    }

    static class MeleeHelper
    {
        internal static void FilterTargets(ref List<RaycastHit> hits)
        {
            HashSet<IHittable> uniqueTargets = [];
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].transform.TryGetComponent(out IHittable hittable))
                {
                    if (!uniqueTargets.Add(hittable))
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered duplicate shovel hit on \"{hits[i].transform.name}\"");
                        continue;
                    }

                    EnemyAICollisionDetect enemyAICollisionDetect = hittable as EnemyAICollisionDetect;
                    // this prevents the shovel from bouncing off thumper/spider/etc. hurtboxes, since that wouldn't actually deal damage
                    if (enemyAICollisionDetect != null && enemyAICollisionDetect.onlyCollideWhenGrounded)
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered invalid melee target on \"{hits[i].transform.name}\"");
                    }
                }
            }
        }
    }
}