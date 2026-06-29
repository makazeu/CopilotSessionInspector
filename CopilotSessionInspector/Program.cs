using CopilotSessionInspector.Components;
using CopilotSessionInspector.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Copilot Session Inspector services.
builder.Services.AddSingleton<CopilotPaths>();
builder.Services.AddSingleton<SessionStoreService>();
builder.Services.AddSingleton<TelemetryLogParser>();
builder.Services.AddSingleton<SessionEventsParser>();
builder.Services.AddSingleton<SessionAnalysisService>();
builder.Services.AddHostedService<SessionCacheWarmupService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
