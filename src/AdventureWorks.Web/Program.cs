using AdventureWorks.Web.Components;
using AdventureWorks.Web.Models;
using AdventureWorks.Web.Services;
using EfCore.ConsumerTrace;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Factory so interactive components can create a short-lived context per operation; a circuit-scoped
// context would throw if a user triggers a second query (e.g. paging) while one is still running.
// AddDbContextFactory also registers AdventureWorksContext itself as a scoped service.
builder.Services.AddDbContextFactory<AdventureWorksContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AdventureWorks"))
        .AddInterceptors(new ConsumerTraceInterceptor()));

builder.Services.AddSingleton<RecentQueryFeed>();

WebApplication app = builder.Build();

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
