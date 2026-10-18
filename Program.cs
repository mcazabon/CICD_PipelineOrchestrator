using CICD_PipelineOrchestrator.Components;
using CICD_PipelineOrchestrator.Data;
using CICD_PipelineOrchestrator.Services;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();
builder.Services.AddSingleton<GitLabAgentService>();
builder.Services.AddSingleton<PipelineService>();
builder.Services.AddSingleton<GitLabRepositoryService>();
builder.Services.AddSingleton<GitHubRepositoryService>();
builder.Services.AddDbContextFactory<RepositoryCatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("RepositoryCatalog")));
builder.Services.AddSingleton<RepositoryCatalogService>(sp =>
    new RepositoryCatalogService(
        sp.GetRequiredService<IDbContextFactory<RepositoryCatalogDbContext>>(),
        sp.GetService<GitLabRepositoryService>(),
        sp.GetService<GitHubRepositoryService>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        builder.Configuration));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<RepositoryCatalogDbContext>>().CreateDbContext());
builder.Services.AddHttpClient("GitLab");
builder.Services.AddHttpClient("GitHub");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<RepositoryCatalogDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.MapHub<CICD_PipelineOrchestrator.Hubs.DuoAgentHub>("/hub/duoagent");

app.Run();
