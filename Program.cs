using CICD_PipelineOrchestrator.Components;
using CICD_PipelineOrchestrator.Data;
using CICD_PipelineOrchestrator.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<PipelineService>();
builder.Services.AddSingleton<GitLabRepositoryService>();
builder.Services.AddSingleton<GitHubRepositoryService>();
builder.Services.AddDbContextFactory<RepositoryCatalogDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("RepositoryCatalog")));
builder.Services.AddSingleton<RepositoryCatalogService>();
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

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
