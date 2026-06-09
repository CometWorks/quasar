using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace Quasar.Agent
{
    internal static class AgentProfilerTranspiler
    {
        private static readonly ConcurrentDictionary<MethodBase, Candidate[]> CandidatesByMethod =
            new ConcurrentDictionary<MethodBase, Candidate[]>();

        private static readonly MethodInfo BeginCallSiteMethod =
            AccessTools.Method(typeof(AgentProfiler), nameof(AgentProfiler.BeginCallSite));

        private static readonly MethodInfo EndCallSiteMethod =
            AccessTools.Method(typeof(AgentProfiler), nameof(AgentProfiler.EndCallSite));

        public static Candidate CreateCandidate(Type declaringType, string methodNameRegex, string category)
        {
            return new Candidate(declaringType, methodNameRegex, category);
        }

        public static void Clear()
        {
            CandidatesByMethod.Clear();
        }

        public static bool Patch(Harmony harmony, MethodBase original, string patchName, IEnumerable<Candidate> candidates)
        {
            if (harmony == null || original == null)
                return false;

            var candidateArray = candidates.Where(candidate => candidate != null).ToArray();
            if (candidateArray.Length == 0)
                return false;

            try
            {
                CandidatesByMethod[original] = candidateArray;
                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(AgentProfilerTranspiler), nameof(Transpile)));
                harmony.Patch(original, transpiler: transpiler);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar deep profiler patch skipped: {patchName}: {exception.Message}");
                return false;
            }
        }

        private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
        {
            if (!CandidatesByMethod.TryGetValue(__originalMethod, out var candidates))
                return instructions;

            var found = 0;
            var input = instructions.ToList();
            var output = new List<CodeInstruction>(input.Count);
            for (var index = 0; index < input.Count; index++)
            {
                var instruction = input[index];
                if (TryGetTargetMethod(instruction, out var targetMethod) &&
                    !HasCallPrefix(input, index) &&
                    TryFindCandidate(targetMethod, candidates, out var candidate) &&
                    TryBuildInstrumentation(targetMethod, candidate.Category, generator, instruction, out var before, out var after))
                {
                    found++;
                    output.AddRange(before);
                    output.Add(instruction);
                    output.AddRange(after);
                    continue;
                }

                output.Add(instruction);
            }

            if (found == 0)
                Console.WriteLine($"Quasar deep profiler found no call sites in {FormatMethod(__originalMethod)}");

            return output;
        }

        private static bool TryBuildInstrumentation(
            MethodBase targetMethod,
            string category,
            ILGenerator generator,
            CodeInstruction callInstruction,
            out List<CodeInstruction> before,
            out List<CodeInstruction> after)
        {
            before = null;
            after = null;

            if (targetMethod == null || generator == null || targetMethod.ContainsGenericParameters)
                return false;

            if (targetMethod is MethodInfo methodInfo &&
                (methodInfo.ReturnType.IsByRef || methodInfo.ReturnType.IsPointer))
                return false;

            if (!targetMethod.IsStatic && targetMethod.DeclaringType != null && targetMethod.DeclaringType.IsValueType)
                return false;

            var parameters = targetMethod.GetParameters();
            if (parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer))
                return false;

            var tokenLocal = generator.DeclareLocal(typeof(AgentProfilerToken));
            var parameterLocals = new LocalBuilder[parameters.Length];
            before = new List<CodeInstruction>();
            after = new List<CodeInstruction>();

            for (var index = parameters.Length - 1; index >= 0; index--)
            {
                var local = generator.DeclareLocal(parameters[index].ParameterType);
                parameterLocals[index] = local;
                before.Add(new CodeInstruction(OpCodes.Stloc, local));
            }

            if (targetMethod.IsStatic)
                before.Add(new CodeInstruction(OpCodes.Ldnull));
            else
                before.Add(new CodeInstruction(OpCodes.Dup));

            var callSiteId = AgentProfiler.RegisterCallSite(category, targetMethod);
            before.Add(new CodeInstruction(OpCodes.Ldc_I4, callSiteId));
            before.Add(new CodeInstruction(OpCodes.Call, BeginCallSiteMethod));
            before.Add(new CodeInstruction(OpCodes.Stloc, tokenLocal));

            foreach (var local in parameterLocals)
                before.Add(new CodeInstruction(OpCodes.Ldloc, local));

            MoveLabels(callInstruction, before);
            after.Add(new CodeInstruction(OpCodes.Ldloc, tokenLocal));
            after.Add(new CodeInstruction(OpCodes.Call, EndCallSiteMethod));
            return true;
        }

        private static bool HasCallPrefix(IList<CodeInstruction> instructions, int callIndex)
        {
            return callIndex > 0 && instructions[callIndex - 1].opcode.FlowControl == FlowControl.Meta;
        }

        private static void MoveLabels(CodeInstruction callInstruction, List<CodeInstruction> before)
        {
            if (callInstruction.labels == null || callInstruction.labels.Count == 0 || before.Count == 0)
                return;

            before[0].labels.AddRange(callInstruction.labels);
            callInstruction.labels.Clear();
        }

        private static bool TryGetTargetMethod(CodeInstruction instruction, out MethodBase method)
        {
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                instruction.operand is MethodBase targetMethod)
            {
                method = targetMethod;
                return true;
            }

            method = null;
            return false;
        }

        private static bool TryFindCandidate(MethodBase targetMethod, IEnumerable<Candidate> candidates, out Candidate match)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Matches(targetMethod))
                {
                    match = candidate;
                    return true;
                }
            }

            match = null;
            return false;
        }

        private static string FormatMethod(MethodBase method)
        {
            if (method == null)
                return "<unknown>";

            return $"{method.DeclaringType?.FullName ?? "<unknown>"}#{method.Name}";
        }

        internal sealed class Candidate
        {
            private readonly Type _declaringType;
            private readonly Regex _methodNameRegex;

            public Candidate(Type declaringType, string methodNameRegex, string category)
            {
                _declaringType = declaringType;
                _methodNameRegex = new Regex(methodNameRegex, RegexOptions.CultureInvariant);
                Category = category;
            }

            public string Category { get; }

            public bool Matches(MethodBase method)
            {
                if (method == null || method.DeclaringType == null)
                    return false;

                if (_declaringType != null && !_declaringType.IsAssignableFrom(method.DeclaringType))
                    return false;

                return _methodNameRegex.IsMatch(method.Name);
            }
        }
    }
}
