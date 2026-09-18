// Phase 1 skeleton: builds, starts and answers /health. Lane P2B replaces this file.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var app = builder.Build();
app.MapHealthChecks("/health");
app.Run();
