using System.Collections.Generic;

namespace ApiForge.Models
{
    /// <summary>
    /// Represents the state of an agent during execution.
    /// Represents the mutable state that flows through the analysis pipeline.
    /// </summary>
    public class AgentState
    {
        /// <summary>
        /// The ID of the master node in the DAG.
        /// </summary>
        public string? MasterNode { get; set; }

        /// <summary>
        /// The ID of the node currently being processed.
        /// </summary>
        public string? InProcessNode { get; set; }

        /// <summary>
        /// List of node IDs that are waiting to be processed.
        /// </summary>
        public List<string> ToBeProcessedNodes { get; set; } = new List<string>();

        /// <summary>
        /// Dynamic parts of the node currently being processed.
        /// </summary>
        public List<string> InProcessNodeDynamicParts { get; set; } = new List<string>();

        /// <summary>
        /// The URL associated with the current action.
        /// </summary>
        public string ActionUrl { get; set; } = string.Empty;

        /// <summary>
        /// Input variables provided for the agent's execution.
        /// </summary>
        public Dictionary<string, string> InputVariables { get; set; } = new Dictionary<string, string>();
    }
}
