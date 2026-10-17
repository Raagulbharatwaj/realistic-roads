using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace RealisticRoads
{
    // Harmony lets you run your own code before/after the game's methods.
    public static class Patcher
    {
        private const string HarmonyId = "com.raagul.realisticroads";
        private static bool patched;

        public static void PatchAll()
        {
            if (patched) return;
            var harmony = new Harmony(HarmonyId);
            int failures = 0;
            // One class at a time, so a patch that can't find its target (e.g.
            // after a game update) only switches off that one feature.
            foreach (var type in typeof(Patcher).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception e)
                {
                    failures++;
                    Mod.LogError("Patch " + type.Name + " failed, that feature is off", e);
                }
            }
            patched = true;
            foreach (var method in harmony.GetPatchedMethods()) Mod.Log("Patched " + Describe(method));
            Mod.Log("Harmony patches applied" + (failures > 0 ? " (" + failures + " failed)" : ""));
        }

        public static void UnpatchAll()
        {
            try
            {
                new Harmony(HarmonyId).UnpatchAll(HarmonyId);
                if (patched) Mod.Log("Harmony patches removed");
            }
            catch (Exception e)
            {
                Mod.LogError("Removing Harmony patches failed", e);
            }
            patched = false;
        }

        internal static string Describe(MethodBase method)
        {
            var parts = new List<string>();
            foreach (var p in method.GetParameters()) parts.Add(p.ParameterType.Name + " " + p.Name);
            return method.DeclaringType.Name + "." + method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }
    }

    // Example patch: runs every time the game creates a road segment.
    // Proves you can hook into road building, which a curve tool needs.
    // NOTE: CreateSegment runs on the simulation thread. Only thread-safe code here.
    [HarmonyPatch]
    public static class CreateSegmentPatch
    {
        // One overload calls the other, so a single new segment passes through
        // this patch twice with the same id. Remember the last id logged on this
        // thread and skip the repeat.
        [ThreadStatic] private static ushort lastLogged;

        // The game has more than one CreateSegment overload (with and without
        // TreeInfo), and a plain [HarmonyPatch(typeof(NetManager), "CreateSegment")]
        // throws when the name is ambiguous. Patch every overload that starts with
        // "out ushort segment" and returns bool instead.
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(NetManager).GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != "CreateSegment" || method.ReturnType != typeof(bool)) continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0) continue;
                var first = parameters[0];
                if (first.Name == "segment" && first.IsOut && first.ParameterType == typeof(ushort).MakeByRefType())
                    yield return method;
            }
        }

        // "segment" is an out parameter in the game, so Harmony needs "ref"
        public static void Postfix(bool __result, ref ushort segment)
        {
            try
            {
                if (!__result || segment == lastLogged) return;
                lastLogged = segment;
                Mod.Log("Road segment created: #" + segment + DescribeBridge(segment));
            }
            catch (Exception e)
            {
                Mod.LogError("CreateSegment postfix failed", e);
            }
        }

        // For suspension bridges (m_doubleLength): the length and which ends
        // are towers or middle nodes, to compare our bridges with vanilla ones.
        private static string DescribeBridge(ushort segment)
        {
            var net = NetManager.instance;
            var s = net.m_segments.m_buffer[segment];
            var info = s.Info;
            if (info == null || info.m_netAI == null) return "";
            var field = info.m_netAI.GetType().GetField("m_doubleLength", BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(bool) || !(bool)field.GetValue(info.m_netAI)) return "";
            var nodes = net.m_nodes.m_buffer;
            float length = CurveMath.FlatDistance(nodes[s.m_startNode].m_position, nodes[s.m_endNode].m_position);
            return " (" + info.name + ", " + length.ToString("F0") + " m, start node #" + s.m_startNode +
                   " " + NodeState(nodes[s.m_startNode]) + ", end node #" + s.m_endNode + " " + NodeState(nodes[s.m_endNode]) + ")";
        }

        private static string NodeState(NetNode node)
        {
            bool middle = (node.m_flags & NetNode.Flags.Double) != NetNode.Flags.None;
            return (middle ? "middle" : "tower") + " elev " + node.m_elevation;
        }
    }

    // Smooth splits: replaces the corners of the three road ends at a node the
    // player marked (see SmoothSplits). Every other node is left untouched.
    // Runs last so it wins over other corner patches (e.g. Node Controller) on
    // marked nodes only. Called from several threads; must stay thread-safe.
    [HarmonyPatch(typeof(NetSegment), nameof(NetSegment.CalculateCorner),
        new[] { typeof(ushort), typeof(bool), typeof(bool), typeof(bool), typeof(UnityEngine.Vector3), typeof(UnityEngine.Vector3), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out, ArgumentType.Out })]
    [HarmonyPriority(Priority.Last)]
    public static class CalculateCornerPatch
    {
        // Nodes whose corner failed once (logged once each; the call falls back
        // to the vanilla corner). Not a global switch: one road being rebuilt
        // mid-call must not turn off every smooth split in the city.
        private static readonly HashSet<ushort> logged = new HashSet<ushort>();

        public static void Postfix(ushort segmentID, bool start, bool leftSide,
            ref UnityEngine.Vector3 cornerPos, ref UnityEngine.Vector3 cornerDirection)
        {
            ushort node = 0;
            try
            {
                var segments = NetManager.instance.m_segments.m_buffer;
                node = start ? segments[segmentID].m_startNode : segments[segmentID].m_endNode;
                // CS2-style junctions: road ends moved back for rounder corners.
                if (Junctions.TryAdjustCorner(segmentID, node, ref cornerPos, cornerDirection)) return;

                if (!SmoothSplits.IsEnabled(node)) return;

                UnityEngine.Vector3 position, direction;
                if (!SmoothSplits.TryGetCorner(segmentID, node, leftSide, out position, out direction)) return;

                // Keep the game's height handling: same height above the road
                // centre as the vanilla corner had above the node.
                float nodeY = NetManager.instance.m_nodes.m_buffer[node].m_position.y;
                position.y += cornerPos.y - nodeY;
                direction.y = cornerDirection.y;

                cornerPos = position;
                cornerDirection = direction;
            }
            catch (Exception e)
            {
                bool first;
                lock (logged) first = logged.Count < 32 && logged.Add(node);
                if (first) Mod.LogError("Corner at node #" + node + " failed; vanilla corner used there", e);
            }
        }
    }

    // Smooth splits and CS2-style junctions: don't draw the vanilla junction
    // pieces (seam strip, crosswalks, median blocks) close up. Like Node
    // Controller, only the check "should this piece be drawn here"
    // (NetInfo.Node.CheckFlags) answers no for our nodes, inside the private
    // RenderInstance that draws one junction piece. Every loop and the render
    // list walk stay exactly as the game wrote them: making the piece list look
    // empty instead broke that walk ("Invalid list detected!" every frame).
    [HarmonyPatch]
    public static class NodeRenderPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(NetNode).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "RenderInstance") continue;
                var parameters = method.GetParameters();
                if (parameters.Length > 1 && parameters[1].Name == "nodeID" && parameters[1].ParameterType == typeof(ushort))
                    yield return method;
            }
        }

        // Public overload: untouched. Private one: CheckFlags redirected.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            if (original.IsPublic) return instructions;
            return NodeMeshes.HidePieces(instructions, original);
        }

        // After the node is drawn (the public overload, once per node and
        // frame): our own junction surface.
        public static void Postfix(ushort nodeID, MethodBase __originalMethod, object[] __args)
        {
            // Public overload: (cameraInfo, nodeID, layerMask). If a game update
            // changes that, draw in every pass rather than never.
            if (!__originalMethod.IsPublic) return;
            JunctionRenderer.Render(nodeID, __args.Length > 2 && __args[2] is int ? (int)__args[2] : -1);
        }
    }

    public static class NodeMeshes
    {
        // Replaces "node.CheckFlags(flags)" with NodeMeshes.CheckFlags(node,
        // flags, nodeID): the stack keeps its shape, so nothing else changes.
        public static IEnumerable<CodeInstruction> HidePieces(IEnumerable<CodeInstruction> instructions, MethodBase method)
        {
            var original = typeof(NetInfo.Node).GetMethod("CheckFlags", new[] { typeof(NetNode.Flags) });
            var replacement = typeof(NodeMeshes).GetMethod(nameof(CheckFlags), BindingFlags.Public | BindingFlags.Static);
            int nodeIdArg = -1;
            var parameters = method.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
                if (parameters[i].Name == "nodeID" && parameters[i].ParameterType == typeof(ushort)) nodeIdArg = i + 1;   // arg 0 is the node itself
            var result = new List<CodeInstruction>();
            int count = 0;
            foreach (var instruction in instructions)
            {
                if (original != null && nodeIdArg > 0 && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    Equals(instruction.operand, original))
                {
                    // Jumps to the old call now land on the added load.
                    var load = new CodeInstruction(OpCodes.Ldarg_S, (byte)nodeIdArg);
                    load.labels.AddRange(instruction.labels);
                    load.blocks.AddRange(instruction.blocks);
                    result.Add(load);
                    result.Add(new CodeInstruction(OpCodes.Call, replacement));
                    count++;
                    continue;
                }
                result.Add(instruction);
            }
            Mod.Log("Junction piece checks redirected in " + method.Name + ": " + count +
                    (count == 0 ? " (vanilla junction pieces stay visible under smooth splits and CS2-style junctions)" : ""));
            return result;
        }

        public static bool CheckFlags(NetInfo.Node node, NetNode.Flags flags, ushort nodeID)
        {
            if (!node.CheckFlags(flags)) return false;
            try
            {
                return !(SmoothSplits.IsEnabled(nodeID) || Junctions.IsEnabled(nodeID));
            }
            catch
            {
                return true;
            }
        }

        // After every "info.m_nodes.Length", pass the length and the node
        // through 'filter', which returns 0 for nodes whose pieces we hide.
        public static IEnumerable<CodeInstruction> HideNodeMeshes(IEnumerable<CodeInstruction> instructions, OpCode loadNodeId, MethodInfo filter)
        {
            var nodesField = typeof(NetInfo).GetField("m_nodes");
            bool previousLoadsNodes = false;
            int count = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (previousLoadsNodes && instruction.opcode == OpCodes.Ldlen)
                {
                    yield return new CodeInstruction(loadNodeId);
                    yield return new CodeInstruction(OpCodes.Call, filter);
                    count++;
                }
                previousLoadsNodes = instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, nodesField);
            }
            Mod.Log("Junction piece lookups redirected: " + count);
        }

        // Far away only smooth splits are hidden; CS2-style junctions keep the
        // vanilla far-away piece (ours is only drawn close up).
        public static int FarLength(int length, ushort nodeID)
        {
            try
            {
                return SmoothSplits.IsEnabled(nodeID) ? 0 : length;
            }
            catch
            {
                return length;
            }
        }
    }

    // Smooth splits seen from far away: the game draws junctions into
    // combined LOD meshes (CalculateGroupData / PopulateGroupData), which the
    // RenderInstance prefix doesn't reach. Like Node Controller, make the
    // road's list of junction pieces look empty for our nodes there. Both
    // methods must agree, or the LOD buffers are sized wrong.
    [HarmonyPatch]
    public static class NodeGroupDataPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(NetNode).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "CalculateGroupData" && method.Name != "PopulateGroupData") continue;
                var parameters = method.GetParameters();
                if (parameters.Length > 0 && parameters[0].Name == "nodeID" && parameters[0].ParameterType == typeof(ushort))
                    yield return method;
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return NodeMeshes.HideNodeMeshes(instructions, OpCodes.Ldarg_1,
                typeof(NodeMeshes).GetMethod(nameof(NodeMeshes.FarLength), BindingFlags.Public | BindingFlags.Static));
        }
    }
}
