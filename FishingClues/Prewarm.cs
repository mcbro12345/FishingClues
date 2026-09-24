using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using KamiToolKit.Nodes;

using FishingClues.Base;
using FishingClues.Game.Data;
using FishingClues.UI.Windows;

namespace FishingClues;

// does the slow first-time work on a background thread when the plugin loads, so the first
// time the journal opens costs what any later open does
public static class Prewarm
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static void Run()
    {
        try
        {
            JournalBuilder.WarmSheets();
            foreach (var assembly in new[] { typeof(Plugin).Assembly, typeof(TextureButtonNode).Assembly })
                Compile(assembly);
            if (Services.ClientState.IsLoggedIn) NativeJournalWindow.PrewarmMapCache(Services.ClientState.TerritoryType);
        }
        catch (Exception ex)
        {
            Services.Log.Debug(ex, "[FishingClues] Prewarm stopped early.");
        }
    }

    // JIT-compiles every method up front, otherwise each one is compiled the first time it runs
    private static void Compile(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).ToArray()!; }
        foreach (var type in types.Where(t => !t.ContainsGenericParameters))
        {
            foreach (MethodBase method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters) continue;
                try { RuntimeHelpers.PrepareMethod(method.MethodHandle); }
                catch { }
            }
        }
    }
}
