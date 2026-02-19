using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ApiForge.Models
{
    /// <summary>
    /// Represents a node within the DAG.
    /// </summary>
    public class DagNode
    {
        /// <summary>
        /// Unique identifier for this node.
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// The type of this node (e.g., "cookie", "master", "cURL", "not found").
        /// </summary>
        public string NodeType { get; set; } = string.Empty;

        /// <summary>
        /// Optional content associated with this node.
        /// </summary>
        public Dictionary<string, object>? Content { get; set; }

        /// <summary>
        /// Optional list of dynamic parts for this node.
        /// </summary>
        public List<string>? DynamicParts { get; set; }

        /// <summary>
        /// Optional list of extracted parts for this node.
        /// </summary>
        public List<string>? ExtractedParts { get; set; }

        /// <summary>
        /// Optional input variables for this node.
        /// </summary>
        public Dictionary<string, string>? InputVariables { get; set; }

        public override string ToString()
        {
            var parts = new List<string>
            {
                $"NodeType={NodeType}"
            };
            if (Content != null)
                parts.Add($"Content=[{Content.Count} entries]");
            if (DynamicParts != null)
                parts.Add($"DynamicParts=[{string.Join(", ", DynamicParts)}]");
            if (ExtractedParts != null)
                parts.Add($"ExtractedParts=[{string.Join(", ", ExtractedParts)}]");
            if (InputVariables != null)
                parts.Add($"InputVariables=[{string.Join(", ", InputVariables.Select(kv => $"{kv.Key}={kv.Value}"))}]");

            return $"{Id}: {{{string.Join(", ", parts)}}}";
        }
    }

    /// <summary>
    /// Manages a Directed Acyclic Graph (DAG) using dictionary-based adjacency lists.
    /// Uses a simple dictionary-based graph implementation for lightweight DAG management.
    /// </summary>
    public class DagManager
    {
        /// <summary>
        /// Valid node types.
        /// </summary>
        public static readonly HashSet<string> NodeTypes = new HashSet<string>
        {
            "cookie", "master", "cURL", "not found"
        };

        /// <summary>
        /// All nodes in the graph, keyed by node ID.
        /// </summary>
        private readonly Dictionary<string, DagNode> _nodes = new Dictionary<string, DagNode>();

        /// <summary>
        /// Forward adjacency list: fromNodeId -> set of toNodeIds.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _forwardEdges = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Reverse adjacency list: toNodeId -> set of fromNodeIds.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _reverseEdges = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Optional root node ID.
        /// </summary>
        public string? RootId { get; set; }

        /// <summary>
        /// Adds a new node to the DAG and returns its unique ID.
        /// </summary>
        /// <param name="nodeType">The type of node (e.g., "cookie", "master", "cURL", "not found").</param>
        /// <param name="content">Optional content dictionary.</param>
        /// <param name="dynamicParts">Optional list of dynamic parts.</param>
        /// <param name="extractedParts">Optional list of extracted parts.</param>
        /// <param name="inputVariables">Optional input variables.</param>
        /// <returns>The string ID of the newly created node.</returns>
        public string AddNode(
            string nodeType,
            Dictionary<string, object>? content = null,
            List<string>? dynamicParts = null,
            List<string>? extractedParts = null,
            Dictionary<string, string>? inputVariables = null)
        {
            var nodeId = Guid.NewGuid().ToString();

            var node = new DagNode
            {
                Id = nodeId,
                NodeType = nodeType,
                Content = content,
                DynamicParts = dynamicParts,
                ExtractedParts = extractedParts,
                InputVariables = inputVariables
            };

            _nodes[nodeId] = node;
            _forwardEdges[nodeId] = new HashSet<string>();
            _reverseEdges[nodeId] = new HashSet<string>();

            return nodeId;
        }

        /// <summary>
        /// Updates the attributes of an existing node. Only non-null values are applied.
        /// </summary>
        /// <param name="nodeId">The ID of the node to update.</param>
        /// <param name="nodeType">If provided, updates the node type.</param>
        /// <param name="content">If provided, updates the content dictionary.</param>
        /// <param name="dynamicParts">If provided, updates the dynamic parts.</param>
        /// <param name="extractedParts">If provided, updates the extracted parts.</param>
        /// <param name="inputVariables">If provided, updates the input variables.</param>
        /// <exception cref="KeyNotFoundException">Thrown if the node does not exist.</exception>
        public void UpdateNode(
            string nodeId,
            string? nodeType = null,
            Dictionary<string, object>? content = null,
            List<string>? dynamicParts = null,
            List<string>? extractedParts = null,
            Dictionary<string, string>? inputVariables = null)
        {
            if (!_nodes.TryGetValue(nodeId, out var node))
            {
                throw new KeyNotFoundException($"Node '{nodeId}' does not exist in the graph.");
            }

            if (nodeType != null)
                node.NodeType = nodeType;
            if (content != null)
                node.Content = content;
            if (dynamicParts != null)
                node.DynamicParts = dynamicParts;
            if (extractedParts != null)
                node.ExtractedParts = extractedParts;
            if (inputVariables != null)
                node.InputVariables = inputVariables;
        }

        /// <summary>
        /// Adds a directed edge from one node to another.
        /// </summary>
        /// <param name="fromNodeId">The source node ID.</param>
        /// <param name="toNodeId">The target node ID.</param>
        /// <exception cref="KeyNotFoundException">Thrown if either node does not exist.</exception>
        public void AddEdge(string fromNodeId, string toNodeId)
        {
            if (!_nodes.ContainsKey(fromNodeId))
                throw new KeyNotFoundException($"Source node '{fromNodeId}' does not exist in the graph.");
            if (!_nodes.ContainsKey(toNodeId))
                throw new KeyNotFoundException($"Target node '{toNodeId}' does not exist in the graph.");

            _forwardEdges[fromNodeId].Add(toNodeId);
            _reverseEdges[toNodeId].Add(fromNodeId);
        }

        /// <summary>
        /// Retrieves a node by its ID, or null if it does not exist.
        /// </summary>
        /// <param name="nodeId">The ID of the node to retrieve.</param>
        /// <returns>The DagNode, or null if not found.</returns>
        public DagNode? GetNode(string nodeId)
        {
            return _nodes.TryGetValue(nodeId, out var node) ? node : null;
        }

        /// <summary>
        /// Returns the IDs of all successor (child) nodes of the given node.
        /// </summary>
        /// <param name="nodeId">The ID of the node whose successors to retrieve.</param>
        /// <returns>A list of successor node IDs.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the node does not exist.</exception>
        public List<string> GetSuccessors(string nodeId)
        {
            if (!_forwardEdges.TryGetValue(nodeId, out var successors))
                throw new KeyNotFoundException($"Node '{nodeId}' does not exist in the graph.");

            return successors.ToList();
        }

        /// <summary>
        /// Returns the IDs of all predecessor (parent) nodes of the given node.
        /// </summary>
        /// <param name="nodeId">The ID of the node whose predecessors to retrieve.</param>
        /// <returns>A list of predecessor node IDs.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the node does not exist.</exception>
        public List<string> GetPredecessors(string nodeId)
        {
            if (!_reverseEdges.TryGetValue(nodeId, out var predecessors))
                throw new KeyNotFoundException($"Node '{nodeId}' does not exist in the graph.");

            return predecessors.ToList();
        }

        /// <summary>
        /// Detects cycles in the DAG using depth-first search.
        /// </summary>
        /// <returns>
        /// A list of (fromNodeId, toNodeId) tuples forming a cycle (the back edge(s) found),
        /// or null if no cycles are detected.
        /// </returns>
        public List<(string From, string To)>? DetectCycles()
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();
            var parent = new Dictionary<string, string?>();
            var cycleEdges = new List<(string From, string To)>();

            foreach (var nodeId in _nodes.Keys)
            {
                if (!visited.Contains(nodeId))
                {
                    if (DfsCycleDetect(nodeId, visited, recursionStack, parent, cycleEdges))
                    {
                        return cycleEdges;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Returns all source nodes (nodes with in-degree of 0, i.e., no incoming edges).
        /// </summary>
        /// <returns>A list of DagNode objects that have no predecessors.</returns>
        public List<DagNode> GetSourceNodes()
        {
            var sourceNodes = new List<DagNode>();

            foreach (var kvp in _nodes)
            {
                if (!_reverseEdges.TryGetValue(kvp.Key, out var predecessors) || predecessors.Count == 0)
                {
                    sourceNodes.Add(kvp.Value);
                }
            }

            return sourceNodes;
        }

        /// <summary>
        /// Returns the in-degree (number of incoming edges) for the given node.
        /// </summary>
        /// <param name="nodeId">The ID of the node.</param>
        /// <returns>The count of incoming edges.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the node does not exist.</exception>
        public int GetInDegree(string nodeId)
        {
            if (!_reverseEdges.TryGetValue(nodeId, out var predecessors))
                throw new KeyNotFoundException($"Node '{nodeId}' does not exist in the graph.");

            return predecessors.Count;
        }

        /// <summary>
        /// Returns a string representation of all nodes and their attributes.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach (var kvp in _nodes)
            {
                sb.AppendLine(kvp.Value.ToString());
            }

            return sb.ToString().TrimEnd();
        }

        private bool DfsCycleDetect(
            string nodeId,
            HashSet<string> visited,
            HashSet<string> recursionStack,
            Dictionary<string, string?> parent,
            List<(string From, string To)> cycleEdges)
        {
            visited.Add(nodeId);
            recursionStack.Add(nodeId);

            if (_forwardEdges.TryGetValue(nodeId, out var successors))
            {
                foreach (var successor in successors)
                {
                    if (!visited.Contains(successor))
                    {
                        parent[successor] = nodeId;
                        if (DfsCycleDetect(successor, visited, recursionStack, parent, cycleEdges))
                        {
                            return true;
                        }
                    }
                    else if (recursionStack.Contains(successor))
                    {
                        // Back edge found: cycle detected
                        cycleEdges.Add((nodeId, successor));
                        return true;
                    }
                }
            }

            recursionStack.Remove(nodeId);
            return false;
        }
    }
}
