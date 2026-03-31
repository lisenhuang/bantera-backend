var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Bantera API",
        Version = "v1"
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Bantera API v1");
    options.RoutePrefix = "swagger";
});

app.MapGet("/", () => "Hello World!")
    .WithName("HelloWorld");

app.MapGet("/version", () =>
{
    var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    return Results.Ok(new { version });
})
.WithName("GetVersion");

app.Run();
