using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox;
using Sandbox.Game.Entities.Blocks;

namespace Quasar.Agent
{
    internal static class AgentProfilerPatches
    {
        private static Harmony _harmony;
        private static bool _applied;
        private static AgentProfilerMode _appliedMode = AgentProfilerMode.Off;
        private static readonly ConcurrentDictionary<MethodBase, int> CallSites = new ConcurrentDictionary<MethodBase, int>();

        public static void Apply(AgentOptions options)
        {
            var mode = options?.ProfilerMode ?? AgentProfilerMode.SafeContinuous;
            if (_applied)
                return;

            _applied = true;
            _appliedMode = mode;
            if (mode == AgentProfilerMode.Off)
            {
                Console.WriteLine("Quasar profiler patches disabled.");
                return;
            }

            try
            {
                _harmony = new Harmony("quasar.agent.profiler");
                var deepMode = mode == AgentProfilerMode.DeepContinuous;
                var patched = PatchKnownMethods(deepMode);
                if (deepMode)
                    patched += PatchDeepCallSites();

                Console.WriteLine($"Quasar profiler patches applied: {patched} ({(deepMode ? "deep continuous" : "safe continuous high-level")})");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar profiler patches failed: {exception.Message}");
            }
        }

        public static void Reconfigure(AgentProfilerMode mode)
        {
            AgentProfiler.SetMode(mode);

            if (_applied && _appliedMode == mode)
                return;

            Dispose();
            Apply(new AgentOptions { ProfilerMode = mode });
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
                _appliedMode = AgentProfilerMode.Off;
                CallSites.Clear();
                AgentProfilerTranspiler.Clear();
            }
        }

        private static int PatchKnownMethods(bool deepMode)
        {
            var count = 0;
            count += PatchDeclared(typeof(MySandboxGame), "RunSingleFrame", "frame") ? 1 : 0;
            count += PatchDeclared(typeof(MySandboxGame), "UpdateInternal", "update") ? 1 : 0;
            count += Patch(AccessTools.Method(typeof(MyProgrammableBlock), "RunSandboxedProgramAction"), "script") ? 1 : 0;

            count += PatchDeclaredByTypeName("Sandbox.Engine.Physics.MyPhysics", "Simulate", "physics") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Physics.MyPhysics", "StepWorldsInternal", "physics") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Networking.MyGameService", "Update", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "UpdateBefore", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "UpdateAfter", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "SendUpdate", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Multiplayer.MyTransportLayer", "Tick", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Networking.MyNetworkReader", "Process", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Multiplayer.MyDedicatedServer", "ReportReplicatedObjects", "replication") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Engine.Multiplayer.MyDedicatedServer", "Tick", "network") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.Multiplayer.MyGpsCollection", "Update", "gps") ? 1 : 0;
            count += PatchDeclaredByTypeName("Sandbox.Game.Multiplayer.MyPlayerCollection", "SendDirtyBlockLimits", "network") ? 1 : 0;

            if (deepMode)
            {
                count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "OnClientAcks", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "OnClientUpdate", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "ReplicableReady", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "ReplicableRequest", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("VRage.Network.MyReplicationServer", "OnEvent", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("Sandbox.Engine.Multiplayer.MyDedicatedServerBase", "ClientConnected", "networkEvent") ? 1 : 0;
                count += PatchDeclaredByTypeName("Sandbox.Engine.Multiplayer.MyMultiplayerServerBase", "ClientReady", "networkEvent") ? 1 : 0;
            }

            if (!deepMode)
                count += PatchDeclaredByTypeName("Sandbox.Game.World.MySession", "UpdateComponents", "session") ? 1 : 0;

            return count;
        }

        private static int PatchDeepCallSites()
        {
            var count = 0;

            count += PatchSessionUpdateCallSites() ? 1 : 0;
            if (PatchSessionComponentCallSites())
            {
                count++;
            }
            else if (PatchDeclaredByTypeName("Sandbox.Game.World.MySession", "UpdateComponents", "session"))
            {
                Console.WriteLine("Quasar deep profiler session call-site patches missed; using safe session method timing.");
                count++;
            }

            var entityCallSiteCount = PatchGameLogicCallSites() + PatchParallelEntityCallSites();
            if (entityCallSiteCount == 0)
            {
                Console.WriteLine("Quasar deep profiler entity call-site patches missed; keeping high-level timing only.");
            }

            count += entityCallSiteCount;
            count += PatchPhysicsCallSites() ? 1 : 0;
            return count;
        }

        private static bool PatchSessionUpdateCallSites()
        {
            var session = AccessTools.TypeByName("Sandbox.Game.World.MySession");
            var scheduler = AccessTools.TypeByName("ParallelTasks.IWorkScheduler");
            var parallel = AccessTools.TypeByName("ParallelTasks.Parallel");
            return PatchCallSites(
                FindDeclaredMethod(session, "Update"),
                "MySession.Update",
                NewCandidates(
                    Candidate(scheduler, "^WaitForTasksToFinish$", "parallelWait"),
                    Candidate(parallel, "^RunCallbacks$", "parallelRun")));
        }

        private static bool PatchSessionComponentCallSites()
        {
            var session = AccessTools.TypeByName("Sandbox.Game.World.MySession");
            var component = AccessTools.TypeByName("VRage.Game.Components.MySessionComponentBase");
            var replicationLayer = AccessTools.TypeByName("VRage.Network.MyReplicationLayer");
            return PatchCallSites(
                FindDeclaredMethod(session, "UpdateComponents"),
                "MySession.UpdateComponents",
                NewCandidates(
                    Candidate(component, "^UpdatedBeforeInit$", "session"),
                    Candidate(component, "^UpdateBeforeSimulation$", "session"),
                    Candidate(replicationLayer, "^Simulate$", "replication"),
                    Candidate(component, "^Simulate$", "session"),
                    Candidate(component, "^UpdateAfterSimulation$", "session")));
        }

        private static int PatchGameLogicCallSites()
        {
            var type = AccessTools.TypeByName("VRage.Game.Entity.MyGameLogic");
            var count = 0;
            foreach (var methodName in new[] { "UpdateOnceBeforeFrame", "UpdateBeforeSimulation", "UpdateAfterSimulation" })
            {
                if (PatchCallSites(
                        FindDeclaredMethod(type, methodName),
                        $"MyGameLogic.{methodName}",
                        EntityUpdateCandidates()))
                {
                    count++;
                }
            }

            return count;
        }

        private static int PatchParallelEntityCallSites()
        {
            var type = AccessTools.TypeByName("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator");
            var count = 0;
            var methods = new[]
            {
                "DispatchOnceBeforeFrame",
                "DispatchBeforeSimulation",
                "DispatchSimulate",
                "DispatchAfterSimulation",
                "UpdateBeforeSimulation",
                "UpdateBeforeSimulation10",
                "UpdateBeforeSimulation100",
                "ParallelUpdateHandlerBeforeSimulation",
                "ParallelUpdateHandlerAfterSimulation",
                "UpdateAfterSimulation",
                "UpdateAfterSimulation10",
                "UpdateAfterSimulation100",
            };

            foreach (var methodName in methods)
            {
                if (PatchCallSites(
                        FindDeclaredMethod(type, methodName),
                        $"MyParallelEntityUpdateOrchestrator.{methodName}",
                        EntityUpdateCandidates()))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool PatchPhysicsCallSites()
        {
            var type = AccessTools.TypeByName("Sandbox.Engine.Physics.MyPhysics");
            return PatchCallSites(
                FindDeclaredMethod(type, "StepWorldsParallel"),
                "MyPhysics.StepWorldsParallel",
                NewCandidates(
                    Candidate(null, "^ExecuteJobQueue$", "physicsDetail"),
                    Candidate(null, "^IsClusterActive$", "physicsDetail"),
                    Candidate(null, "^ProcessAllJobs$", "physicsDetail"),
                    Candidate(null, "^WaitForCompletion$", "physicsDetail"),
                    Candidate(null, "^FinishMtStep$", "physicsDetail")));
        }

        private static IEnumerable<AgentProfilerTranspiler.Candidate> EntityUpdateCandidates()
        {
            return NewCandidates(
                Candidate(null, "^UpdateBeforeSimulation.*$", "entity"),
                Candidate(null, "^UpdateAfterSimulation.*$", "entity"),
                Candidate(null, "^UpdateBeforeSimulationParallel$", "entity"),
                Candidate(null, "^UpdateAfterSimulationParallel$", "entity"),
                Candidate(null, "^UpdateOnceBeforeFrame$", "entity"),
                Candidate(null, "^Simulate$", "entity"));
        }

        private static bool PatchCallSites(MethodBase method, string patchName, IEnumerable<AgentProfilerTranspiler.Candidate> candidates)
        {
            if (method == null)
            {
                Console.WriteLine($"Quasar deep profiler patch skipped: {patchName}: target method not found");
                return false;
            }

            return AgentProfilerTranspiler.Patch(_harmony, method, patchName, candidates);
        }

        private static AgentProfilerTranspiler.Candidate Candidate(Type type, string methodNameRegex, string category)
        {
            return AgentProfilerTranspiler.CreateCandidate(type, methodNameRegex, category);
        }

        private static IEnumerable<AgentProfilerTranspiler.Candidate> NewCandidates(params AgentProfilerTranspiler.Candidate[] candidates)
        {
            return candidates.Where(candidate => candidate != null);
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
                CallSites[method] = AgentProfiler.RegisterCallSite(category, method);
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
            var callSiteId = CallSites.TryGetValue(__originalMethod, out var value) ? value : 0;
            AgentProfiler.End(__state, callSiteId, __instance);
        }

        private static void StaticPostfix(long __state, MethodBase __originalMethod)
        {
            var callSiteId = CallSites.TryGetValue(__originalMethod, out var value) ? value : 0;
            AgentProfiler.End(__state, callSiteId, null);
        }

    }
}
