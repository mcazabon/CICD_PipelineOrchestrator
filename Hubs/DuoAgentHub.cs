using System.Text;
using CICD_PipelineOrchestrator.Services;
using CICD_PipelineOrchestrator.Models;
using Microsoft.AspNetCore.SignalR;

namespace CICD_PipelineOrchestrator.Hubs;

public class DuoAgentHub(GitLabAgentService agent, RepositoryCatalogService catalog) : Hub
{
    private readonly GitLabAgentService _agent = agent;
    private readonly RepositoryCatalogService _catalog = catalog;

    public async Task StartFlow(string prompt, string? projectId)
    {
        string? overrideToken = null;
        string? overrideAuth = null;
        var resolvedAuthScheme = "Auto";

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            // attempt to resolve agent credentials for the project
            var creds = _catalog.GetAgentCredentialsForProject(projectId);
            if (creds is not null)
            {
                overrideToken = creds.Value.AgentToken;
                overrideAuth = creds.Value.AuthScheme;
                resolvedAuthScheme = string.IsNullOrWhiteSpace(creds.Value.AuthScheme) ? "Auto" : creds.Value.AuthScheme;
            }
        }

        if (!string.IsNullOrWhiteSpace(overrideAuth))
        {
            resolvedAuthScheme = overrideAuth;
        }

        var (success, workflowId, message) = await _agent.TriggerFlowAsync(prompt, projectId, overrideToken, resolvedAuthScheme, CancellationToken.None);
        // record invocation audit
        try
        {
            var sourceId = string.Empty;
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var cfg = _catalog.GetAgentCredentialsForProject(projectId);
                if (cfg is not null)
                {
                    sourceId = _catalog.ResolveSourceConfigurationId(projectId);
                }
            }

            using var db = _catalog.GetDbContextFactory().CreateDbContext();
            db.AgentInvocations.Add(new Data.Entities.AgentInvocationEntity
            {
                SourceConfigurationId = string.IsNullOrWhiteSpace(sourceId) ? (projectId ?? string.Empty) : sourceId,
                ProjectReference = projectId,
                InvokedBy = Context.User?.Identity?.Name ?? "(anonymous)",
                InvokedUtc = DateTime.UtcNow,
                WorkflowId = workflowId,
                Success = success,
                Message = message ?? string.Empty,
                AuthScheme = resolvedAuthScheme
            });
            db.SaveChanges();
        }
        catch
        {
            // Do not block on audit failures
        }
        if (!success)
        {
            await Clients.Caller.SendAsync("OnTrace", $"ERROR: {message}");
            return;
        }

        await Clients.Caller.SendAsync("OnTrace", $"Workflow started: {workflowId}");

        if (string.IsNullOrWhiteSpace(workflowId))
        {
            await Clients.Caller.SendAsync("OnTrace", "No workflow id returned; cannot stream trace.");
            return;
        }

        // Fire-and-forget streaming loop so hub method returns quickly
        _ = Task.Run(async () =>
        {
            try
            {
                using var stream = await _agent.GetTraceStreamAsync(workflowId, overrideToken, resolvedAuthScheme, CancellationToken.None);
                if (stream is null)
                {
                    await Clients.Caller.SendAsync("OnTrace", "No trace available for this workflow.");
                    return;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    await Clients.Caller.SendAsync("OnTrace", line);
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("OnTrace", $"Trace streaming error: {ex.Message}");
            }
        });
    }
}
