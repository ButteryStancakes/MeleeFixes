using BepInEx;
using HarmonyLib;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using UnityEngine;

namespace MeleeFixes
{
    [BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        const string PLUGIN_GUID = "butterystancakes.lethalcompany.meleefixes", PLUGIN_NAME = "Melee Fixes", PLUGIN_VERSION = "1.0.0";
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
        [HarmonyPatch(typeof(Shovel), nameof(Shovel.HitShovel)), HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(KnifeItem), nameof(KnifeItem.HitKnife))]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> ShovelTransHitShovel(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = instructions.ToList();

            bool triggerCheck = false;
            int insertAt = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                //Plugin.Logger.LogInfo(codes[i]);

                // gameObject.layer == 11
                if (!triggerCheck && codes[i].opcode == OpCodes.Callvirt && (MethodInfo)codes[i].operand == typeof(GameObject).GetMethod($"get_{nameof(GameObject.layer)}", BindingFlags.Instance | BindingFlags.Public) && codes[i + 1].opcode == OpCodes.Ldc_I4_S && (sbyte)codes[i + 1].operand == 11)
                {
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (codes[j].opcode == OpCodes.Ldarg_0)
                        {
                            List<CodeInstruction> clone = new();
                            // clone all opcodes up to RaycastHit.transform
                            for (int k = j; codes[k].opcode != OpCodes.Call || (MethodInfo)codes[k].operand != typeof(RaycastHit).GetMethod($"get_{nameof(RaycastHit.transform)}", BindingFlags.Instance | BindingFlags.Public); k++)
                                clone.Add(new CodeInstruction(codes[k].opcode, codes[k].operand));
                            // RaycastHit.collider
                            clone.Add(new CodeInstruction(OpCodes.Call, typeof(RaycastHit).GetMethod($"get_{nameof(RaycastHit.collider)}", BindingFlags.Instance | BindingFlags.Public)));
                            // Collider.isTrigger
                            clone.Add(new CodeInstruction(OpCodes.Callvirt, typeof(Collider).GetMethod($"get_{nameof(Collider.isTrigger)}", BindingFlags.Instance | BindingFlags.Public)));
                            // == false
                            clone.Add(new CodeInstruction(OpCodes.Brtrue, codes[i + 2].operand));

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
                                            Plugin.Logger.LogInfo("Transpiler: Add isTrigger check after layer checks");
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
                else if (codes[i].opcode == OpCodes.Call)
                {
                    MethodInfo methodInfo = (MethodInfo)codes[i].operand;
                    // Physics.Linecast
                    if (insertAt == -1 && methodInfo.Name.Equals(nameof(Physics.Linecast)))
                        insertAt = i;
                    // FindObjectOfType<RoundManager>()
                    else if (methodInfo.Name.Equals("FindObjectOfType") && methodInfo.ReturnType == typeof(RoundManager))
                    {
                        codes[i].operand = typeof(RoundManager).GetMethod($"get_{nameof(RoundManager.Instance)}", BindingFlags.Static | BindingFlags.Public);
                        Plugin.Logger.LogInfo("Transpiler: Replace FindObjectOfType<RoundManager>() with RoundManager.Instance");
                    }
                }
            }

            if (insertAt > -1)
            {
                codes[insertAt].operand = typeof(Physics).GetMethods().First(method => method.Name.Equals(nameof(Physics.Linecast)) && method.GetParameters().Length == 5);
                codes.Insert(insertAt, new CodeInstruction(OpCodes.Ldc_I4_1, null));
                Plugin.Logger.LogInfo("Transpiler: Add QueryTriggerInteraction.Ignore to Physics.Linecast");
            }

            return codes;
        }
    }
}