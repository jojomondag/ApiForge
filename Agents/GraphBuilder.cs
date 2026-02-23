using ApiForge.Models;
using ApiForge.Utilities;

namespace ApiForge.Agents;

/// <summary>
/// Builds and executes the integration analysis pipeline.
/// Builds and executes the integration analysis pipeline.
///
/// The pipeline follows this order:
///   1. EndUrlIdentifyAgent
///   2. UrlToCurl
///   3. Loop (up to maxSteps):
///      a. DynamicPartIdentifyingAgent
///      b. InputVariablesIdentifyingAgent
///      c. FindCurlFromContent
///      d. Check end condition: if ToBeProcessedNodes is empty -> break, else -> continue
/// </summary>
public class GraphBuilder
{
    /// <summary>
    /// Builds the integration agent and runs the full analysis pipeline.
    /// </summary>
    /// <param name="prompt">The user prompt describing the action to analyze.</param>
    /// <param name="harFilePath">Path to the HAR file containing network requests.</param>
    /// <param name="cookiePath">Path to the cookies JSON file.</param>
    /// <param name="llmService">The LLM service instance for making model calls.</param>
    /// <param name="inputVariables">Optional input variables to seed the agent state.</param>
    /// <param name="maxSteps">
    /// Maximum total node executions (default 20).
    /// The first 2 steps are consumed by
    /// EndUrlIdentifyAgent and UrlToCurl, then each loop iteration consumes 3 more
    /// (DynamicPartIdentifying, InputVariablesIdentifying, FindCurlFromContent).
    /// </param>
    /// <param name="toGenerateCode">Whether to generate integration code at the end.</param>
    /// <returns>A tuple of the completed DagManager and the IntegrationAgent.</returns>
    public static async Task<(DagManager Dag, IntegrationAgent Agent)> BuildAndRunAsync(
        string prompt,
        string harFilePath,
        string cookiePath,
        LlmService llmService,
        Dictionary<string, string>? inputVariables = null,
        int maxSteps = 20,
        bool toGenerateCode = false)
    {
        // Create the agent
        var agent = new IntegrationAgent(prompt, harFilePath, cookiePath, llmService);

        // Initialize the agent state
        var state = new AgentState
        {
            MasterNode = null,
            InProcessNode = null,
            ToBeProcessedNodes = new List<string>(),
            InProcessNodeDynamicParts = new List<string>(),
            ActionUrl = string.Empty,
            InputVariables = inputVariables ?? new Dictionary<string, string>()
        };

        // Step 1: Identify the end URL
        Console.WriteLine("[GraphBuilder] Running EndUrlIdentifyAgent...");
        state = await agent.EndUrlIdentifyAgentAsync(state);

        // Step 2: Convert URL to cURL
        Console.WriteLine("[GraphBuilder] Running UrlToCurl...");
        state = await agent.UrlToCurlAsync(state);

        // Step 3: Loop through the processing pipeline.
        // Track total node executions.
        // The 2 initial steps already consumed 2 of the budget.
        int totalNodeExecutions = 2;
        bool pipelineAborted = false;
        while (totalNodeExecutions + 3 <= maxSteps)
        {
            int loopIteration = (totalNodeExecutions - 2) / 3 + 1;

            try
            {
                Console.WriteLine($"[GraphBuilder] Iteration {loopIteration} (step {totalNodeExecutions + 1}/{maxSteps}) - Running DynamicPartIdentifyingAgent...");
                state = await agent.DynamicPartIdentifyingAgentAsync(state);

                Console.WriteLine($"[GraphBuilder] Iteration {loopIteration} (step {totalNodeExecutions + 2}/{maxSteps}) - Running InputVariablesIdentifyingAgent...");
                state = await agent.InputVariablesIdentifyingAgentAsync(state);

                Console.WriteLine($"[GraphBuilder] Iteration {loopIteration} (step {totalNodeExecutions + 3}/{maxSteps}) - Running FindCurlFromContent...");
                state = await agent.FindCurlFromContentAsync(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GraphBuilder] LLM-fel i iteration {loopIteration}: {ex.Message}");
                Console.WriteLine("[GraphBuilder] Visar delresultat...");
                pipelineAborted = true;
                break;
            }

            totalNodeExecutions += 3;

            // Check end condition
            string endConditionResult = CheckEndCondition(state, agent, toGenerateCode);

            if (endConditionResult == "end")
            {
                // Pipeline complete - print and optionally generate code
                await PrintFinalDagAsync(agent, toGenerateCode, llmService);
                break;
            }
            else
            {
                // Continue - print the current state of the graph
                Console.WriteLine("Continuing execution");
                if (agent.GlobalMasterNodeId != null)
                {
                    Console.Write("Generated graph at current step: ");
                    DagPrinter.PrintDag(agent.DagManager, agent.GlobalMasterNodeId);
                }
            }
        }

        // Show partial results if pipeline was aborted
        if (pipelineAborted && agent.GlobalMasterNodeId != null)
        {
            Console.WriteLine();
            Console.WriteLine("=== Delresultat (pipeline avbröts) ===");
            DagPrinter.PrintDag(agent.DagManager, agent.GlobalMasterNodeId);
        }

        return (agent.DagManager, agent);
    }

    /// <summary>
    /// Checks whether the pipeline should end or continue.
    /// Determines whether the pipeline should end or continue.
    /// </summary>
    /// <param name="state">The current agent state.</param>
    /// <param name="agent">The integration agent.</param>
    /// <param name="toGenerateCode">Whether code generation is requested.</param>
    /// <returns>"end" if processing is complete, "continue" otherwise.</returns>
    private static string CheckEndCondition(AgentState state, IntegrationAgent agent, bool toGenerateCode)
    {
        // Detect cycles in the DAG
        agent.DagManager.DetectCycles();

        if (state.ToBeProcessedNodes.Count == 0)
        {
            Console.WriteLine("------------------------Successfully analyzed!!!-------------------------------");
            return "end";
        }
        else
        {
            return "continue";
        }
    }

    /// <summary>
    /// Prints the final DAG and optionally generates code.
    /// Called when the pipeline completes successfully.
    /// </summary>
    /// <param name="agent">The integration agent.</param>
    /// <param name="toGenerateCode">Whether to generate integration code.</param>
    /// <param name="llmService">The LLM service for code generation.</param>
    private static async Task PrintFinalDagAsync(IntegrationAgent agent, bool toGenerateCode, LlmService llmService)
    {
        if (agent.GlobalMasterNodeId != null)
        {
            DagPrinter.PrintDag(agent.DagManager, agent.GlobalMasterNodeId);
        }

        await DagPrinter.PrintDagInReverse(
            agent.DagManager,
            toGenerateCode: toGenerateCode,
            llm: toGenerateCode ? llmService : null);
    }
}
