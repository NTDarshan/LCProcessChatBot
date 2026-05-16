using backend.Hubs;
using backend.Repositories;
using backend.Services;
using backend.Services.SqlGeneration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Register controllers
builder.Services.AddControllers();

// Register OpenAPI / Swagger
builder.Services.AddOpenApi();

// Allow Angular dev server to call this API
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins(
                "http://localhost:4200",
                "https://dev-amidbapps.myarcelormittal.com",
                "https://amidbapps.myarcelormittal.com")
              .AllowCredentials()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

const string AuthScheme = "Bearer";

builder.Services.AddAuthentication(AuthScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://login.microsoftonline.com/common/v2.0";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = true,
            ValidAudience = builder.Configuration["AzureAd:ClientId"] ?? "e06e8f79-2f05-43fa-b9e2-b889e84c052d"
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();
                logger.LogError("JWT auth failed: {Error}", context.Exception.Message);
                return Task.CompletedTask;
            },
            OnChallenge = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();
                logger.LogWarning("JWT challenge on {Path}: {Error} – {Desc}",
                    context.Request.Path, context.Error, context.ErrorDescription);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 102_400;
});

// Register memory cache service
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 500;
    options.CompactionPercentage = 0.25;
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(2);
});

// Register application services (scoped = per-request lifetime)
builder.Services.AddScoped<ISqlRepository, SqlRepository>();
builder.Services.AddScoped<CacheService>();
builder.Services.AddScoped<DomainValidationService>();
builder.Services.AddScoped<AiUnderstandingService>();
builder.Services.AddScoped<AiResponseService>();
builder.Services.AddScoped<EntityExtractionService>();
builder.Services.AddScoped<IntentRouterService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<ChatHistoryService>();
builder.Services.AddScoped<SqlValidationService>();
// SqlGeneration sub-providers — registered before SqlGenerationService
builder.Services.AddScoped<SchemaProvider>();
builder.Services.AddScoped<QueryRuleProvider>();
builder.Services.AddScoped<ResponseTypeGuideProvider>();
builder.Services.AddScoped<SqlGenerationService>();
builder.Services.AddScoped<SuggestedQuestionsService>();
builder.Services.AddScoped<ClarificationService>();
builder.Services.AddScoped<ChatService>();

var app = builder.Build();

// Configure Swagger UI in development
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "LC ChatBot API v1");
    });
}

app.UseHttpsRedirection();
app.UseWebSockets();
app.UseCors("AllowAngular");
app.UseAuthentication();
app.UseAuthorization();
app.MapHub<LcChatHub>("/hubs/chat");
app.MapControllers();
app.Run();
