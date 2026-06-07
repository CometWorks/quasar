using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;

namespace Quasar.Agent
{
    internal static class AgentProfilerPatches
    {
        private static Harmony _harmony;
        private static bool _applied;
        private static readonly ConcurrentDictionary<MethodBase, string> Categories = new ConcurrentDictionary<MethodBase, string>();

        public static void Apply()
        {
            if (_applied)
                return;

            _applied = true;
            try
            {
                _harmony = new Harmony("quasar.agent.profiler");
                var patched = PatchKnownMethods() + PatchEntityUpdateMethods();
                Console.WriteLine($"Quasar profiler patches applied: {patched}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar profiler patches failed: {exception.Message}");
            }
        }

        public static void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll("quasar.agent.profiler");
            }
            catch
            {
            }
            finally
            {
                _harmony = null;
                _applied = false;
            }
        }

        private static int PatchKnownMethods()
        {
            var count = 0;
            count += PatchDeclared(typeof(MySandboxGame), "RunSingleFrame", "frame") ? 1 : 0;
            count += PatchDeclared(typeof(MySandboxGame), "UpdateInternal", "update") ? 1 : 0;
            count += Patch(AccessTools.Method(typeof(MyProgrammableBlock), "RunSandboxedProgramAction"), "script") ? 1 : 0;

            count += PatchDeclaredByTypeName("Sandbox.Engine.Physics.MyPhysics", "Simulate", "physics") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Physics.MyPhysics", "StepWorldsInternal", "physics") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.Replication.MyReplicationServer", "UpdateBefore", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.Replication.MyReplicationServer", "UpdateAfter", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.Replication.MyReplicationServer", "SendUpdate", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("VRage.Network.MyTransportLayer", "Tick", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("VRage.Network.MyNetworkReader", "Process", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.World.MySession", "UpdateComponents", "session") ? 1 : 0;
            return count;
        }

        private static int PatchEntityUpdateMethods()
        {
            var entityBase = typeof(MyCubeGrid).BaseType;
            if (entityBase == null)
                return 0;

            var count = 0;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var type in assemblies.SelectMany(GetTypesSafely))
            {
                if (type == null || type.IsAbstract || !entityBase.IsAssignableFrom(type))
                    continue;

                foreach (var method in FindDeclaredUpdateMethods(type))
                {
                    var category = typeof(MyCubeGrid).IsAssignableFrom(type) ? "grid" : "entity";
                    if (Patch(method, category))
                        count++;
                }
            }

            return count;
        }

        private static IEnumerable<MethodInfo> FindDeclaredUpdateMethods(Type type)
        {
            var names = new[]
            {
                "UpdateBeforeSimulation",
                "UpdateBeforeSimulation10",
                "UpdateBeforeSimulation100",
                "UpdateAfterSimulation",
                "UpdateAfterSimulation10",
                "UpdateAfterSimulation100",
                "UpdateOnceBeforeFrame",
                "Simulate",
            };

            foreach (var name in names)
            {
                var method = AccessTools.Method(type, name, Type.EmptyTypes);
                if (method != null && method.DeclaringType == type)
                    yield return method;
            }
        }

        private static bool PatchDeclaredByTypeName(string typeName, string methodName, string category)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
                return false;

            return PatchDeclared(type, methodName, category);
        }

        private static bool PatchDeclared(Type type, string methodName, string category)
        {
            return Patch(FindDeclaredMethod(type, methodName), category);
        }

        private static MethodInfo FindDeclaredMethod(Type type, string methodName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var current = type; current != null; current = current.BaseType)
            {
                try
                {
                    var method = current.GetMethods(flags).FirstOrDefault(candidate => candidate.Name == methodName);
                    if (method != null)
                        return method;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static bool Patch(MethodBase method, string category)
        {
            if (method == null || _harmony == null)
                return false;

            try
            {
                Categories[method] = category;
                var prefix = new HarmonyMethod(typeof(AgentProfilerPatches).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                var postfixMethod = method.IsStatic
                    ? nameof(StaticPostfix)
                    : nameof(InstancePostfix);
                var postfix = new HarmonyMethod(typeof(AgentProfilerPatches).GetMethod(postfixMethod, BindingFlags.Static | BindingFlags.NonPublic));
                _harmony.Patch(method, prefix, postfix);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar profiler patch skipped: {method.DeclaringType?.FullName}.{method.Name}: {exception.Message}");
                return false;
            }
        }

        private static void Prefix(out long __state)
        {
            __state = AgentProfiler.Begin();
        }

        private static void InstancePostfix(long __state, object __instance, MethodBase __originalMethod)
        {
            var category = Categories.TryGetValue(__originalMethod, out var value) ? value : "method";
            AgentProfiler.End(__state, category, __instance, __originalMethod);
        }

        private static void StaticPostfix(long __state, MethodBase __originalMethod)
        {
            var category = Categories.TryGetValue(__originalMethod, out var value) ? value : "method";
            AgentProfiler.End(__state, category, null, __originalMethod);
        }

        private static IEnumerable<Type> GetTypesSafely(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }
    }
}
