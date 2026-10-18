using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CICD_PipelineOrchestrator.Services;

public sealed class GitLabAgentService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<GitLabAgentService> logger)
{
    // Primary constructor parameters are used directly (C# 12 primary constructor)
    // Local aliases for readability
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<GitLabAgentService> _logger = logger;

    private string ResolveAgentToken(string? overrideToken = null)
    {
        var token = !string.IsNullOrWhiteSpace(overrideToken)
            ? overrideToken
            : (_configuration["GitLab:AgentToken"] ?? _configuration["GitLab:Token"] ?? string.Empty);

        return token.Trim();
    }

    private HttpClient CreateClient(bool usePrivateToken = false, string? overrideToken = null)
    {
        var baseUrl = _configuration["GitLab:BaseUrl"] ?? "https://gitlab.com";
        var token = ResolveAgentToken(overrideToken);

        var client = _httpClientFactory.CreateClient("GitLabAgent");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        if (!string.IsNullOrWhiteSpace(token))
        {
            if (usePrivateToken)
            {
                client.DefaultRequestHeaders.Remove("PRIVATE-TOKEN");
                client.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
            }
            else
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<(bool Success, string? WorkflowId, string Message)> TriggerFlowAsync(string prompt, string? projectId, string? overrideToken = null, string? overrideAuthScheme = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return (false, null, "Prompt is empty.");
        }

        // determine configured auth scheme: Bearer, PrivateToken, or Auto. Allow override.
        var configured = string.IsNullOrWhiteSpace(overrideAuthScheme) ? (_configuration["GitLab:AuthScheme"] ?? "Auto") : overrideAuthScheme!;
        configured = string.IsNullOrWhiteSpace(configured) ? "Auto" : configured.Trim();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GitLab auth scheme configured as {Scheme}", configured);
        }

        object? projectIdValue = projectId;
        if (!string.IsNullOrWhiteSpace(projectId) && long.TryParse(projectId, out var numericProjectId))
        {
            projectIdValue = numericProjectId;
        }

        var payload = new Dictionary<string, object?>
        {
            ["goal"] = prompt,
            ["project_id"] = projectIdValue,
            ["workflow_definition"] = "developer/v1",
            ["start_workflow"] = true
        };

        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            HttpResponseMessage response;
            string responseBody;

            // Auth strategy
                if (string.Equals(configured, "PrivateToken", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Using PRIVATE-TOKEN header for GitLab Duo API requests.");
                var pc = CreateClient(usePrivateToken: true, overrideToken: overrideToken);
                response = await pc.PostAsync("api/v4/ai/duo_workflows/workflows", content, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var preview = string.IsNullOrWhiteSpace(responseBody) ? "no response body" : (responseBody.Length > 400 ? responseBody[..400] + "..." : responseBody);
                    var msg = $"Authentication failed (PRIVATE-TOKEN): {(int)response.StatusCode} {response.ReasonPhrase}. {preview} \nAuthentication failed — check GitLab:AgentToken scope (ai_features) and token type (personal vs fine-grained).";
                    _logger.LogWarning("Trigger flow returned 401 using PRIVATE-TOKEN header.");
                    return (false, null, msg);
                }
            }
            else if (string.Equals(configured, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Using Bearer Authorization header for GitLab Duo API requests.");
                var bc = CreateClient(usePrivateToken: false, overrideToken: overrideToken);
                response = await bc.PostAsync("api/v4/ai/duo_workflows/workflows", content, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    var preview = string.IsNullOrWhiteSpace(responseBody) ? "no response body" : (responseBody.Length > 400 ? responseBody[..400] + "..." : responseBody);
                    var msg = $"Authentication failed (Bearer): {(int)response.StatusCode} {response.ReasonPhrase}. {preview} \nAuthentication failed — check GitLab:AgentToken scope (ai_features) and token type (personal vs fine-grained).";
                    _logger.LogWarning("Trigger flow returned 401 using Bearer header.");
                    return (false, null, msg);
                }
            }
            else
            {
                // Auto mode: try Bearer then PRIVATE-TOKEN
                _logger.LogInformation("Auth scheme set to Auto: trying Bearer then PRIVATE-TOKEN on failure.");
                var bc = CreateClient(usePrivateToken: false, overrideToken: overrideToken);
                response = await bc.PostAsync("api/v4/ai/duo_workflows/workflows", content, cancellationToken);
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogInformation("Bearer failed with 401; retrying with PRIVATE-TOKEN header.");
                    var pc = CreateClient(usePrivateToken: true, overrideToken: overrideToken);
                    var retryResponse = await pc.PostAsync("api/v4/ai/duo_workflows/workflows", new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
                    var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken);
                    if (!retryResponse.IsSuccessStatusCode)
                    {
                        var preview = string.IsNullOrWhiteSpace(retryBody) ? "no response body" : (retryBody.Length > 400 ? retryBody[..400] + "..." : retryBody);
                        var msg = $"Trigger flow failed: {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}. {preview} \nAuthentication failed — check GitLab:AgentToken scope (ai_features) and token type (personal vs fine-grained).";
                        _logger.LogWarning("Trigger flow retry with PRIVATE-TOKEN also failed (status {Status}).", (int)retryResponse.StatusCode);
                        return (false, null, msg);
                    }

                    response = retryResponse;
                    responseBody = retryBody;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var preview = string.IsNullOrWhiteSpace(responseBody) ? "no response body" : (responseBody.Length > 400 ? responseBody[..400] + "..." : responseBody);
                return (false, null, $"Trigger flow failed: {(int)response.StatusCode} {response.ReasonPhrase}. {preview}");
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("id", out var idElement))
                {
                    return (true, idElement.GetRawText().Trim('"'), "Workflow triggered.");
                }
                if (doc.RootElement.TryGetProperty("workflow_id", out var wid))
                {
                    return (true, wid.GetRawText().Trim('"'), "Workflow triggered.");
                }
            }
            catch
            {
                // fall through
            }

            return (true, null, "Workflow triggered (no id returned). Response: " + responseBody);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    public async Task<Stream?> GetTraceStreamAsync(string workflowId, string? overrideToken = null, string? overrideAuthScheme = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
        {
            return null;
        }
        // determine configured auth scheme and log
        var configured = string.IsNullOrWhiteSpace(overrideAuthScheme) ? (_configuration["GitLab:AuthScheme"] ?? "Auto") : overrideAuthScheme!;
        configured = string.IsNullOrWhiteSpace(configured) ? "Auto" : configured.Trim();
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("GetTraceStream using auth scheme {Scheme}", configured);
        }

        try
        {
            if (string.Equals(configured, "PrivateToken", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Using PRIVATE-TOKEN header for trace stream.");
                var pc = CreateClient(usePrivateToken: true, overrideToken: overrideToken);
                var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"api/v4/ai/duo_workflows/workflows/{Uri.EscapeDataString(workflowId)}/trace.jsonl");
                var retryResp = await pc.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!retryResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trace stream request failed with status {Status}", (int)retryResp.StatusCode);
                    return null;
                }

                return await retryResp.Content.ReadAsStreamAsync(cancellationToken);
            }

            if (string.Equals(configured, "Bearer", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Using Bearer Authorization header for trace stream.");
                var bc = CreateClient(usePrivateToken: false, overrideToken: overrideToken);
                var request = new HttpRequestMessage(HttpMethod.Get, $"api/v4/ai/duo_workflows/workflows/{Uri.EscapeDataString(workflowId)}/trace.jsonl");
                var resp = await bc.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogWarning("Trace stream unauthorized with Bearer header.");
                    return null;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trace stream request failed with status {Status}", (int)resp.StatusCode);
                    return null;
                }

                return await resp.Content.ReadAsStreamAsync(cancellationToken);
            }

            // Auto: try bearer then private-token
            _logger.LogInformation("Auth scheme Auto: trying Bearer then PRIVATE-TOKEN for trace stream.");
            var bcAuto = CreateClient(usePrivateToken: false, overrideToken: overrideToken);
            var req = new HttpRequestMessage(HttpMethod.Get, $"api/v4/ai/duo_workflows/workflows/{Uri.EscapeDataString(workflowId)}/trace.jsonl");
            var respAuto = await bcAuto.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (respAuto.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogInformation("Bearer unauthorized; retrying trace stream with PRIVATE-TOKEN.");
                var pc = CreateClient(usePrivateToken: true, overrideToken: overrideToken);
                var retryRequest = new HttpRequestMessage(HttpMethod.Get, $"api/v4/ai/duo_workflows/workflows/{Uri.EscapeDataString(workflowId)}/trace.jsonl");
                var retryResp = await pc.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!retryResp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Trace stream retry with PRIVATE-TOKEN failed with status {Status}", (int)retryResp.StatusCode);
                    return null;
                }

                return await retryResp.Content.ReadAsStreamAsync(cancellationToken);
            }

            if (!respAuto.IsSuccessStatusCode)
            {
                _logger.LogWarning("Trace stream request failed with status {Status}", (int)respAuto.StatusCode);
                return null;
            }

            return await respAuto.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trace stream failed for workflow {WorkflowId}", workflowId);
            return null;
        }
    }
}
