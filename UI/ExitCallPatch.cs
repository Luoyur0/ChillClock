// 用户点「结束通话」并确认 → 我们的台词立刻停。
//
// 为什么不靠游戏那个"正在结束通话"的标志：那个标志是演出真正开始时才亮的，
// 中间有一小段延迟，用户会听到"确认都点完了，我们的话还在说"。
// 这里直接挂在游戏处理这个按钮的方法上，抢在它前面把我们的音频停掉。
//
// 方法名是照游戏程序集里的符号定的（OnClickExitButton / OnExitCallTalk），
// 运行时遍历所有类型去找，找不到就什么都不做 —— 不会影响任何原逻辑。
using System;
using System.Reflection;
using HarmonyLib;

namespace ChillFocusWhitelist.UI;

internal static class ExitCallPatch
{
    private static readonly string[] MethodNames = { "OnClickExitButton", "OnExitCallTalk" };

    /// <summary>给游戏里"点结束通话"和"开始告别"这两类方法挂上前缀。</summary>
    public static int Install(Harmony harmony)
    {
        var patched = 0;
        try
        {
            var assembly = typeof(Bulbul.SettingUI).Assembly;
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (Array.IndexOf(MethodNames, method.Name) < 0)
                        continue;
                    if (method.GetParameters().Length != 0)
                        continue;

                    harmony.Patch(method, prefix: new HarmonyMethod(
                        typeof(ExitCallPatch).GetMethod(nameof(Prefix),
                            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)));
                    patched++;
                    Plugin.Log.LogInfo("[Chill Clock] 结束通话拦截已挂：" + type.Name + "." + method.Name);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning("[Chill Clock] 结束通话拦截挂载失败: " + e.Message);
        }

        return patched;
    }

    private static void Prefix()
    {
        try
        {
            Plugin.Instance?.NotifyCallEnding();
        }
        catch
        {
            // 让路失败也不能影响游戏自己的流程
        }
    }
}
