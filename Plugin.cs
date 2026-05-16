using BepInEx;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using UnityEngine;
using GameNetcodeStuff;
using BepInEx.Bootstrap;

namespace MeleeFixes
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    [BepInDependency(GUID_LOBBY_COMPATIBILITY, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PLUGIN_GUID = "butterystancakes.lethalcompany.meleefixes", PLUGIN_NAME = "Melee Fixes", PLUGIN_VERSION = "1.5.1";
        internal static new ManualLogSource Logger;

        const string GUID_LOBBY_COMPATIBILITY = "BMX.LobbyCompatibility";

        void Awake()
        {
            Logger = base.Logger;

            if (Chainloader.PluginInfos.ContainsKey(GUID_LOBBY_COMPATIBILITY))
            {
                Logger.LogInfo("CROSS-COMPATIBILITY - Lobby Compatibility detected");
                LobbyCompatibility.Init();
            }

            new Harmony(PLUGIN_GUID).PatchAll();

            Logger.LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} loaded");
        }
    }

    [HarmonyPatch]
    static class MeleeFixesPatches
    {
        const int TERRAIN = 25;

        static readonly MethodInfo GAME_OBJECT_LAYER = AccessTools.DeclaredPropertyGetter(typeof(GameObject), nameof(GameObject.layer));

        static readonly FieldInfo OBJECTS_HIT_BY_SHOVEL_LIST = AccessTools.Field(typeof(Shovel), nameof(Shovel.objectsHitByShovelList));
        static readonly FieldInfo OBJECTS_HIT_BY_KNIFE_LIST = AccessTools.Field(typeof(KnifeItem), nameof(KnifeItem.objectsHitByKnifeList));
        static readonly FieldInfo ENEMY_COLLIDERS = AccessTools.Field(typeof(ShotgunItem), nameof(ShotgunItem.enemyColliders));

        static readonly MethodInfo FIND_OBJECT_OF_TYPE_ROUND_MANAGER = AccessTools.Method(typeof(Object), nameof(Object.FindObjectOfType), null, [typeof(RoundManager)]);
        static readonly MethodInfo ROUND_MANAGER_INSTANCE = AccessTools.DeclaredPropertyGetter(typeof(RoundManager), nameof(RoundManager.Instance));

        static readonly MethodInfo MELEE_HELPER_FILTER_TARGETS = AccessTools.Method(typeof(WeaponHelper), nameof(WeaponHelper.FilterTargets));
        static readonly MethodInfo SHOTGUN_PRE_PROCESS = AccessTools.Method(typeof(WeaponHelper), nameof(WeaponHelper.ShotgunPreProcess));

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.HitShovel)), HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(KnifeItem), nameof(KnifeItem.HitKnife))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Trans_Hit(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            List<CodeInstruction> codes = instructions.ToList();

            bool filterFunc = false;
            for (int i = 8; i < codes.Count - 3; i++)
            {
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
                        Plugin.Logger.LogDebug($"Transpiler ({__originalMethod.DeclaringType}.{__originalMethod.Name}): Filter targets by validity");
                        filterFunc = true;
                        i += 3;
                    }
                }
                else if (codes[i].opcode == OpCodes.Call && codes[i].operand as MethodInfo == FIND_OBJECT_OF_TYPE_ROUND_MANAGER)
                {
                    codes[i].operand = ROUND_MANAGER_INSTANCE;
                    Plugin.Logger.LogDebug($"Transpiler ({__originalMethod.DeclaringType}.{__originalMethod.Name}): Replace FindObjectOfType<RoundManager>() with RoundManager.Instance");
                }
                else if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand as MethodInfo == GAME_OBJECT_LAYER && codes[i + 1].opcode == OpCodes.Ldc_I4_8 && codes[i - 7].opcode == OpCodes.Ldfld)
                {
                    FieldInfo fieldInfo = (FieldInfo)codes[i - 7].operand;
                    if (fieldInfo == OBJECTS_HIT_BY_SHOVEL_LIST || fieldInfo == OBJECTS_HIT_BY_KNIFE_LIST)
                    {
                        codes.InsertRange(i + 3, [
                            new(OpCodes.Ldarg_0),
                            new(OpCodes.Ldfld, codes[i - 7].operand),
                            new(OpCodes.Ldloc_S, codes[i - 6].operand),
                            new(OpCodes.Callvirt, codes[i - 5].operand),
                            new(OpCodes.Stloc_S, codes[i - 4].operand),
                            new(OpCodes.Ldloca_S, codes[i - 3].operand),
                            new(OpCodes.Call, codes[i - 2].operand),
                            new(OpCodes.Callvirt, codes[i - 1].operand),
                            new(OpCodes.Callvirt, GAME_OBJECT_LAYER),
                            new(OpCodes.Ldc_I4_S, (sbyte)TERRAIN), // new
                            new(OpCodes.Beq, codes[i + 2].operand)
                        ]);
                        Plugin.Logger.LogDebug($"Transpiler ({__originalMethod.DeclaringType}.{__originalMethod.Name}): Bounce off layer 25 (Terrain)");
                        i += 11;
                    }
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(Shovel), nameof(Shovel.ItemActivate))]
        [HarmonyPrefix]
        static void Shovel_Pre_ItemActivate(Shovel __instance)
        {
            __instance.shovelMask |= 1 << TERRAIN;
            if (__instance.reelingUp && __instance.previousPlayerHeldBy != __instance.playerHeldBy)
            {
                __instance.reelingUp = false;
                Plugin.Logger.LogInfo("Reset broken shovel to allow swinging it again");
            }
        }

        [HarmonyPatch(typeof(ShotgunItem), nameof(ShotgunItem.ShootGun))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ShotgunItem_Trans_ShootGun(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            List<CodeInstruction> codes = instructions.ToList();

            bool fixEarsRinging = false;
            for (int i = 2; i < codes.Count; i++)
            {
                // first distance check for tinnitus/screenshake
                if (!fixEarsRinging && codes[i].opcode == OpCodes.Bge_Un && codes[i - 2].opcode == OpCodes.Ldloc_2)
                {
                    int insertAt = -1;
                    for (int j = i + 1; j < codes.Count - 1; j++)
                    {
                        if (codes[j + 1].opcode == OpCodes.Ldloc_2)
                        {
                            // first jump from if/else branches
                            if (insertAt >= 0 && codes[j].opcode == OpCodes.Br)
                            {
                                codes.Insert(insertAt, new(OpCodes.Br, codes[j].operand));
                                Plugin.Logger.LogDebug("Transpiler (Shotgun blast): Fix ear-ringing severity in extremely close range");
                                fixEarsRinging = true;
                                break;
                            }
                            // the end of the first if branch
                            else if (insertAt < 0 && codes[j].opcode == OpCodes.Stloc_S)
                                insertAt = j + 1;
                        }
                    }
                }
                else if (codes[i].opcode == OpCodes.Newarr && (System.Type)codes[i].operand == typeof(RaycastHit) && codes[i - 1].opcode == OpCodes.Ldc_I4_S && (sbyte)codes[i - 1].operand == 10)
                {
                    codes[i - 1].operand = 50;
                    Plugin.Logger.LogDebug("Transpiler (Shotgun blast): Resize target colliders array");
                }
                else if (codes[i].opcode == OpCodes.Call && codes[i].operand.ToString().Contains("SphereCastNonAlloc"))
                {
                    codes.InsertRange(i + 2, [
                        new(OpCodes.Ldarg_1),
                        new(OpCodes.Ldloca_S, codes[i + 1].operand),
                        new(OpCodes.Ldarg_0),
                        new(OpCodes.Ldflda, ENEMY_COLLIDERS),
                        new(OpCodes.Call, SHOTGUN_PRE_PROCESS),
                    ]);
                    Plugin.Logger.LogDebug("Transpiler (Shotgun blast): Pre-process shotgun targets");
                }
            }

            return codes;
        }

        [HarmonyPatch(typeof(KnifeItem), nameof(KnifeItem.EquipItem))]
        [HarmonyPostfix]
        static void KnifeItem_Post_ItemActivate(KnifeItem __instance)
        {
            __instance.knifeMask |= 1 << TERRAIN;
        }
    }

    static class WeaponHelper
    {
        internal static void FilterTargets(ref List<RaycastHit> hits)
        {
            HashSet<IHittable> uniqueTargets = [];
            for (int i = 0; i < hits.Count; i++)
            {
                if (hits[i].transform.TryGetComponent(out IHittable hittable))
                {
                    // temporary until vanilla typo is corrected
                    if (!uniqueTargets.Add(hittable))
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered duplicate shovel hit on \"{hits[i].transform.name}\"");
                        continue;
                    }

                    EnemyAICollisionDetect enemyAICollisionDetect = hittable as EnemyAICollisionDetect;
                    // this prevents the shovel from bouncing off thumper/spider/etc. hurtboxes, since that wouldn't actually deal damage
                    // now also prevents weapons from hitting dead enemies (especially important for the knife, which can't hit multiple targets in a single swing)
                    if (enemyAICollisionDetect != null && ((enemyAICollisionDetect.onlyCollideWhenGrounded && !enemyAICollisionDetect.alwaysAllowHitting) || (enemyAICollisionDetect.mainScript != null && enemyAICollisionDetect.mainScript.isEnemyDead)))
                    {
                        hits.RemoveAt(i--);
                        //Plugin.Logger.LogDebug($"Filtered invalid melee target on \"{hits[i].transform.name}\"");
                    }
                }
            }
        }

        internal static void ShotgunPreProcess(Vector3 shotgunPosition, ref int num, ref RaycastHit[] results)
        {
            int index = 0;
            HashSet<EnemyAI> enemies = [];
            List<RaycastHit> invincibles = [];

            // sort in order of distance
            RaycastHit[] sortedResults = [.. results.Take(num).OrderBy(hit => Vector3.Distance(shotgunPosition, hit.point))];

            // remove all duplicates
            for (int i = 0; i < num; i++)
            {
                if (sortedResults[i].transform.TryGetComponent(out EnemyAICollisionDetect enemyCollider) && (!enemyCollider.onlyCollideWhenGrounded || enemyCollider.alwaysAllowHitting))
                {
                    EnemyAI enemy = enemyCollider.mainScript;
                    if (enemies.Add(enemy))
                    {
                        EnemyType enemyType = enemy.enemyType;
                        // invincible enemies are low-priority
                        if (!enemyType.canDie || enemyType.name == "DocileLocustBees")
                            invincibles.Add(sortedResults[i]);
                        else if (!enemy.isEnemyDead)
                        {
                            results[index] = sortedResults[i];
                            index++;
                            // only hit 10 targets max
                            if (index == 10)
                            {
                                num = 10;
                                return;
                            }
                        }
                    }
                }
            }

            // add invincible enemies at the end, if there are slots leftover
            if (invincibles.Count > 0)
            {
                // slime is "medium priority" since they get angry when shot
                foreach (RaycastHit invincible in invincibles.OrderByDescending(invincible => invincible.transform.GetComponent<EnemyAICollisionDetect>().mainScript is BlobAI))
                {
                    results[index] = invincible;
                    index++;
                    if (index == 10)
                    {
                        num = 10;
                        return;
                    }
                }
            }

            num = index;
        }
    }
}