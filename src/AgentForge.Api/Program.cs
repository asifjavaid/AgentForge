using System.Text.Json.Serialization;
using AgentForge.Api.ErrorHandling;
using AgentForge.Api.Composition;
using AgentForge.Application.Llm;
using AgentForge.Application.Requirements;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<LlmExceptionHandler>();

builder.Services.AddLlmProvider(builder.Configuration);
builder.Services.AddScoped<IRequirementAnalyzer, LlmRequirementAnalyzer>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.MapControllers();

app.Run();

public partial class Program;
