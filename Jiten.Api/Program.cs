using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.PostgreSql;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Api.Authentication;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Jiten.Core.WebNovel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Jiten.Api.Middleware;
using StackExchange.Redis;
using IPNetwork = Microsoft.AspNetCore.HttpOverrides.IPNetwork;

ThreadPool.SetMinThreads(64, 64);

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional: true,
                                  reloadOnChange: true);
builder.Configuration.AddJsonFile("sharedsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Lock down the native image decoder before anything can hand it an uploaded file.
ImageMagickHardening.Configure();

// Suppress verbose HTTP client logging
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.NumberHandling =
        JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1",
                 new Microsoft.OpenApi.Models.OpenApiInfo
                 {
                     Title = "Jiten API", Version = "v1",
                     Description = "OpenAPI documentation for Jiten. Use the Authorize button to provide a Bearer token.",
                     Contact = new Microsoft.OpenApi.Models.OpenApiContact { Name = "Jiten", Url = new Uri("https://jiten.moe") },
                     License = new Microsoft.OpenApi.Models.OpenApiLicense
                               {
                                   Name = "MIT", Url = new Uri("https://opensource.org/licenses/MIT")
                               }
                 });

    c.UseInlineDefinitionsForEnums();
    c.EnableAnnotations();

    c.CustomSchemaIds(SwaggerSchemaId.For);
    c.SchemaFilter<EnumSchemaFilter>();
    c.DocumentFilter<EnumDocumentFilter>();

    // Include XML comments if the XML file exists (improves schemas and descriptions)
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    // JWT Bearer auth definition so Swagger UI shows the lock icon and sends the token
    var securityScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                         {
                             Name = "Authorization", Description = "Enter 'Bearer' [space] and then your JWT token.",
                             In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                             Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
                             Reference = new Microsoft.OpenApi.Models.OpenApiReference
                                         {
                                             Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "Bearer"
                                         }
                         };

    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { { securityScheme, new List<string>() } });

    // API Key auth definition (X-Api-Key header)
    var apiKeyScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                       {
                           Description =
                               "API Key needed to access the endpoints. Use the 'X-Api-Key' header or 'Authorization: ApiKey <key>'.",
                           Name = "X-Api-Key", In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                           Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
                           Reference = new Microsoft.OpenApi.Models.OpenApiReference
                                       {
                                           Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme, Id = "ApiKey"
                                       }
                       };
    c.AddSecurityDefinition("ApiKey", apiKeyScheme);

    // Allow either Bearer OR ApiKey for endpoints
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { { apiKeyScheme, new List<string>() } });
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("Voicevox", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["VoicevoxUrl"] ?? "http://localhost:50021");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<ITtsService, TtsService>();

builder.Services.AddHttpClient(SyosetuSource.HttpClientName, client =>
       {
           client.DefaultRequestHeaders.Add("User-Agent",
                                            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                                            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
           client.Timeout = TimeSpan.FromSeconds(30);
       })
       // The API's gzip=5 body is inflated by hand; this covers the HTML pages
       .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
       {
           AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
       });

builder.Services.AddSingleton<IWebNovelSource, SyosetuSource>();
builder.Services.AddSingleton<IWebNovelSourceResolver, WebNovelSourceResolver>();

// OpenTelemetry Configuration
var otelConfig = builder.Configuration.GetSection("OpenTelemetry");
var enableOtlpExporter = otelConfig.GetValue<bool>("EnableOtlpExporter");

if (enableOtlpExporter)
{
    var serviceName = otelConfig["ServiceName"] ?? "Jiten.Api";
    var serviceVersion = otelConfig["ServiceVersion"] ?? "1.0.0";
    var enableConsoleExporter = otelConfig.GetValue<bool>("EnableConsoleExporter");

    var resourceBuilder = ResourceBuilder.CreateDefault()
                                         .AddService(serviceName: serviceName, serviceVersion: serviceVersion)
                                         .AddAttributes(new Dictionary<string, object>
                                                        {
                                                            ["deployment.environment"] = builder.Environment.EnvironmentName,
                                                            ["host.name"] = Environment.MachineName
                                                        });

    // Configure OpenTelemetry Tracing
    builder.Services.AddOpenTelemetry()
           .WithTracing(tracerProviderBuilder =>
           {
               tracerProviderBuilder
                   .SetResourceBuilder(resourceBuilder)
                   .AddAspNetCoreInstrumentation(options =>
                   {
                       options.RecordException = true;
                       options.Filter = httpContext =>
                       {
                           // Exclude health checks and static files from tracing
                           var path = httpContext.Request.Path.Value ?? "";
                           return !path.Contains("/health") && !path.Contains("/static") && !path.StartsWith("/swagger");
                       };
                   })
                   .AddHttpClientInstrumentation(options => { options.RecordException = true; })
                   .AddEntityFrameworkCoreInstrumentation(options => { options.SetDbStatementForText = true; });

               if (enableConsoleExporter)
               {
                   tracerProviderBuilder.AddConsoleExporter();
               }

               var otlpEndpoint = otelConfig["Otlp:Endpoint"];
               var otlpHeaders = otelConfig["Otlp:Headers"];
               var otlpProtocol = otelConfig["Otlp:Protocol"];

               tracerProviderBuilder.AddOtlpExporter(options =>
               {
                   if (!string.IsNullOrEmpty(otlpEndpoint))
                   {
                       options.Endpoint = new Uri(otlpEndpoint);
                   }

                   if (!string.IsNullOrEmpty(otlpHeaders))
                   {
                       options.Headers = otlpHeaders;
                   }

                   options.Protocol = otlpProtocol?.ToLower() == "http"
                       ? OtlpExportProtocol.HttpProtobuf
                       : OtlpExportProtocol.Grpc;
               });
           })
           .WithMetrics(meterProviderBuilder =>
           {
               meterProviderBuilder
                   .SetResourceBuilder(resourceBuilder)
                   .AddAspNetCoreInstrumentation()
                   .AddHttpClientInstrumentation()
                   .AddRuntimeInstrumentation()
                   .AddMeter(CoverageJourneyService.MeterName)
                   .AddMeter(Jiten.Api.Services.Stripe.BillingTelemetry.MeterName);

               if (enableConsoleExporter)
               {
                   meterProviderBuilder.AddConsoleExporter();
               }

               var otlpEndpoint = otelConfig["Otlp:Endpoint"];
               var otlpHeaders = otelConfig["Otlp:Headers"];
               var otlpProtocol = otelConfig["Otlp:Protocol"];

               meterProviderBuilder.AddOtlpExporter(options =>
               {
                   if (!string.IsNullOrEmpty(otlpEndpoint))
                   {
                       options.Endpoint = new Uri(otlpEndpoint);
                   }

                   if (!string.IsNullOrEmpty(otlpHeaders))
                   {
                       options.Headers = otlpHeaders;
                   }

                   options.Protocol = otlpProtocol?.ToLower() == "http"
                       ? OtlpExportProtocol.HttpProtobuf
                       : OtlpExportProtocol.Grpc;
               });
           });

    // Configure OpenTelemetry Logging
    builder.Logging.ClearProviders();
    // Keep a console sink alongside OTLP: when the OTLP pipeline is the only sink, an outage or a
    // process death leaves `docker logs` empty and the incident unreconstructable.
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        options.UseUtcTimestamp = true;
    });
    builder.Logging.AddOpenTelemetry(options =>
    {
        options.SetResourceBuilder(resourceBuilder);
        options.IncludeFormattedMessage = true;
        options.IncludeScopes = true;
        options.ParseStateValues = true;

        if (enableConsoleExporter)
        {
            options.AddConsoleExporter();
        }

        var otlpEndpoint = otelConfig["Otlp:Endpoint"];
        var otlpHeaders = otelConfig["Otlp:Headers"];
        var otlpProtocol = otelConfig["Otlp:Protocol"];

        options.AddOtlpExporter(exporterOptions =>
        {
            if (!string.IsNullOrEmpty(otlpEndpoint))
            {
                exporterOptions.Endpoint = new Uri(otlpEndpoint);
            }

            if (!string.IsNullOrEmpty(otlpHeaders))
            {
                exporterOptions.Headers = otlpHeaders;
            }

            exporterOptions.Protocol = otlpProtocol?.ToLower() == "http"
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
        });
    });
}

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContextFactory<JitenDbContext>(options =>
                                                             options.UseNpgsql(builder.Configuration.GetConnectionString("JitenDatabase"),
                                                                               o =>
                                                                               {
                                                                                   o.UseQuerySplittingBehavior(QuerySplittingBehavior
                                                                                       .SplitQuery);
                                                                               }));

    builder.Services.AddDbContextFactory<UserDbContext>(options => options.UseNpgsql(builder.Configuration.GetConnectionString("JitenDatabase"),
                                                                                     o =>
                                                                                     {
                                                                                         o.UseQuerySplittingBehavior(QuerySplittingBehavior
                                                                                             .SplitQuery);
                                                                                     }));
}

// Authentication

builder.Services.AddIdentity<User, IdentityRole>(options =>
       {
           // Password settings
           options.Password.RequireDigit = true;
           options.Password.RequireLowercase = true;
           options.Password.RequireUppercase = true;
           options.Password.RequireNonAlphanumeric = false;
           options.Password.RequiredLength = 10;

           // Lockout settings
           options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
           options.Lockout.MaxFailedAccessAttempts = 3;
           options.Lockout.AllowedForNewUsers = true;

           // User settings
           options.User.RequireUniqueEmail = true;
           options.SignIn.RequireConfirmedEmail = true;
       })
       .AddEntityFrameworkStores<UserDbContext>()
       .AddDefaultTokenProviders();

var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = jwtSettings["Secret"];
if (string.IsNullOrEmpty(secretKey) || secretKey.Length < 32)
{
    throw new
        InvalidOperationException("JWT Secret Key is not configured or is too short. It must be at least 32 characters long for HS256.");
}

var key = Encoding.ASCII.GetBytes(secretKey);

builder.Services.AddAuthentication(options =>
       {
           options.DefaultAuthenticateScheme = "Smart";
           options.DefaultChallengeScheme = "Smart";
           options.DefaultScheme = "Smart";
       })
       .AddPolicyScheme("Smart", "JWT or API Key", options =>
       {
           options.ForwardDefaultSelector = context =>
           {
               if (context.Request.Headers.ContainsKey("X-Api-Key"))
                   return "ApiKey";
               var auth = context.Request.Headers["Authorization"].FirstOrDefault();
               if (!string.IsNullOrEmpty(auth) && auth.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
                   return "ApiKey";
               return JwtBearerDefaults.AuthenticationScheme;
           };
       })
       .AddJwtBearer(options =>
       {
           options.SaveToken = true;
           options.RequireHttpsMetadata = builder.Environment.IsProduction();
           options.TokenValidationParameters = new TokenValidationParameters
                                               {
                                                   ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
                                                   ValidateIssuerSigningKey = true, ValidIssuer = jwtSettings["Issuer"],
                                                   ValidAudience = jwtSettings["Audience"],
                                                   IssuerSigningKey = new SymmetricSecurityKey(key), ClockSkew = TimeSpan.Zero
                                               };
       })
       .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options => { options.HeaderName = "X-Api-Key"; });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("RequiresAdmin", policy => policy.RequireRole(nameof(UserRole.Administrator)));

    // Corpus analysis tools: restricted to users with the Researcher rate-limit tier (or higher),
    // plus administrators. Tier is carried in the "rate_limit_tier" claim by both the JWT and the
    // API-key auth handlers.
    options.AddPolicy("RequiresResearcher", policy => policy.RequireAssertion(ctx =>
        ctx.User.IsInRole(nameof(UserRole.Administrator)) ||
        ctx.User.HasClaim("rate_limit_tier", nameof(RateLimitTier.Researcher)) ||
        ctx.User.HasClaim("rate_limit_tier", nameof(RateLimitTier.Unlimited))));
});

builder.Services.AddScoped<TokenService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddScoped<Microsoft.AspNetCore.Identity.UI.Services.IEmailSender, Jiten.Api.Services.EmailService>();
builder.Services.AddScoped<Jiten.Api.Services.IEmailService, Jiten.Api.Services.EmailService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IJitenPlusService, JitenPlusService>();
builder.Services.Configure<Jiten.Core.Services.CardMediaStorageOptions>(
    builder.Configuration.GetSection(Jiten.Core.Services.CardMediaStorageOptions.SectionName));
builder.Services.AddScoped<ICardMediaQuotaService, CardMediaQuotaService>();
builder.Services.AddScoped<ICardMediaWriteService, CardMediaWriteService>();
builder.Services.AddScoped<IExampleSentenceQueryService, ExampleSentenceQueryService>();
builder.Services.Configure<Jiten.Core.Services.JitenPlusLimitsOptions>(
    builder.Configuration.GetSection(Jiten.Core.Services.JitenPlusLimitsOptions.SectionName));
builder.Services.AddScoped<IUserLimitsService, UserLimitsService>();
builder.Services.AddSingleton<IBillingAlertService, BillingAlertService>();
builder.Services.Configure<Jiten.Api.Services.Stripe.StripeOptions>(builder.Configuration.GetSection("Stripe"));
builder.Services.Configure<Jiten.Api.Services.Legal.LegalDocumentsOptions>(
    builder.Configuration.GetSection(Jiten.Api.Services.Legal.LegalDocumentsOptions.SectionName));
builder.Services.AddSingleton<Jiten.Api.Services.Stripe.IStripeGateway, Jiten.Api.Services.Stripe.StripeGateway>();
builder.Services.AddScoped<Jiten.Api.Services.Stripe.StripeService>();
builder.Services.AddSingleton<IWordFormSiblingCache, WordFormSiblingCache>();
builder.Services.AddSingleton<IDerivationLinkCache, DerivationLinkCache>();
builder.Services.AddSingleton<Jiten.Core.Services.DeckVectorService>();
builder.Services.AddSingleton<Jiten.Core.Services.DescriptionSearchService>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var logger = sp.GetRequiredService<ILogger<Jiten.Core.Services.DescriptionSearchService>>();
    Jiten.Core.Services.SentenceEmbedder? CreateEmbedder()
    {
        var dir = config[Jiten.Core.Services.SentenceEmbedder.ModelDirConfigKey];
        if (Jiten.Core.Services.SentenceEmbedder.IsAvailable(dir))
            return new Jiten.Core.Services.SentenceEmbedder(dir!);
        logger.LogWarning("Description search disabled: no model at DescriptionEmbeddingModelDir='{Dir}'", dir);
        return null;
    }
    return new Jiten.Core.Services.DescriptionSearchService(sp.GetRequiredService<IDbContextFactory<JitenDbContext>>(), CreateEmbedder, logger);
});
builder.Services.AddScoped<IRoadmapDataLoader, RoadmapDataLoader>();
builder.Services.AddScoped<ICoverageJourneyService, CoverageJourneyService>();
builder.Services.AddScoped<IDeckWordResolver, DeckWordResolver>();
builder.Services.AddScoped<IFrequencySourceResolver, FrequencySourceResolver>();
builder.Services.AddScoped<IStudyDeckMembershipService, StudyDeckMembershipService>();
builder.Services.AddScoped<DeckMetadataService>();
builder.Services.AddScoped<IDeckDownloadService, DeckDownloadService>();
builder.Services.AddSingleton<Jiten.Api.Services.ExternalMediaList.ExternalFetchGate>();
builder.Services.AddScoped<Jiten.Api.Services.ExternalMediaList.IExternalMediaListClient, Jiten.Api.Services.ExternalMediaList.ExternalMediaListClient>();
builder.Services.AddScoped<IDeckImportService, DeckImportService>();
builder.Services.AddScoped<IIndexNowService, IndexNowService>();
builder.Services.AddSingleton<ISrsDebounceService, SrsDebounceService>();
builder.Services.AddSingleton<IStudySessionService, StudySessionService>();
builder.Services.AddSingleton<IPendingCoverageQueue, PendingCoverageQueue>();
builder.Services.AddSingleton<IPendingEmbeddingQueue, PendingEmbeddingQueue>();
builder.Services.AddSingleton<IUserActivityTracker, UserActivityTracker>();
builder.Services.AddSingleton<IParseThrottleService, ParseThrottleService>();
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    ConnectionMultiplexer.Connect(sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis")!));
builder.Services.AddScoped<WordReplacementService>();
builder.Services.AddScoped<ICdnService, BunnyCdnService>();
builder.Services.AddScoped<Jiten.Core.Services.RequestActivityService>();
builder.Services.AddScoped<Jiten.Core.Services.NotificationService>();
builder.Services.AddSingleton<StartupReadiness>();
builder.Services.AddHostedService<ParserWarmupService>();
builder.Services.AddHostedService<WordFormSiblingCacheWarmupService>();
builder.Services.AddHostedService<DerivationLinkCacheWarmupService>();
builder.Services.AddHostedService<DeckVectorCacheWarmupService>();

// Shared secret sent by the Nuxt SSR server (X-Internal-Ssr-Key) so first-party server
// rendering is exempt from the per-IP anonymous rate limit. Without this, every anonymous
// SSR request lands in one partition (the SSR host's IP as seen past the reverse proxy) and
// saturates the limit, leaving logged-out pages and OG images data-less. Empty disables it.
var ssrBypassKey = builder.Configuration["SsrBypassKey"];
var ssrBypassKeyBytes = string.IsNullOrEmpty(ssrBypassKey) ? null : Encoding.UTF8.GetBytes(ssrBypassKey);

bool IsTrustedSsr(HttpContext ctx)
{
    if (ssrBypassKeyBytes == null) return false;
    if (!ctx.Request.Headers.TryGetValue("X-Internal-Ssr-Key", out var provided)) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.ToString()), ssrBypassKeyBytes);
}

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("fixed", context =>
    {
        if (IsTrustedSsr(context))
            return RateLimitPartition.GetNoLimiter("ssr-internal");

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tier = context.User.FindFirst("rate_limit_tier")?.Value ?? "Default";

        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        var permitLimit = tier switch
        {
            "Researcher" => 3000,
            "Unlimited" => int.MaxValue,
            _ => 300
        };

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
                                                        _ => new FixedWindowRateLimiterOptions
                                                             {
                                                                 PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(60),
                                                                 QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 3,
                                                                 AutoReplenishment = true
                                                             });
    });

    options.AddPolicy("heavy", context =>
    {
        if (IsTrustedSsr(context))
            return RateLimitPartition.GetNoLimiter("ssr-internal");

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tier = context.User.FindFirst("rate_limit_tier")?.Value ?? "Default";

        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        var permitLimit = tier switch
        {
            "Researcher" => 300,
            "Unlimited" => int.MaxValue,
            _ => userId != null ? 45 : 20
        };

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey,
                                                          _ => new SlidingWindowRateLimiterOptions
                                                               {
                                                                   PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(60),
                                                                   SegmentsPerWindow = 6,
                                                                   QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 2,
                                                                   AutoReplenishment = true
                                                               });
    });

    options.AddPolicy("download", context =>
    {
        if (IsTrustedSsr(context))
            return RateLimitPartition.GetNoLimiter("ssr-internal");

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var tier = context.User.FindFirst("rate_limit_tier")?.Value ?? "Default";

        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        var permitLimit = tier switch
        {
            "Researcher" => 300,
            "Unlimited" => int.MaxValue,
            _ => 10
        };

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey,
                                                          _ => new SlidingWindowRateLimiterOptions
                                                               {
                                                                   PermitLimit = permitLimit, Window = TimeSpan.FromSeconds(60),
                                                                   SegmentsPerWindow = 10,
                                                                   QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 2,
                                                                   AutoReplenishment = true
                                                               });
    });

    options.AddPolicy("compute", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // Single media coverage refresh
    options.AddPolicy("coverage-refresh", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.Get(partitionKey, _ => (RateLimiter)new ChainedRateLimiter(
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3, Window = TimeSpan.FromSeconds(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            }),
            new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30, Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            })));
    });

    options.AddPolicy("journey", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20, Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("freq-list-create", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5, Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("card-media-upload", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromSeconds(60), SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("card-media-import", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        // Set above the client's natural pace (4 workers x ~10-13 req/min) so a normal import never
        // 429s; the concurrency permits below are the real load bound.
        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromSeconds(60), SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("sentence-import", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromSeconds(60), SegmentsPerWindow = 6,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("external-fetch", context =>
    {
        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3, Window = TimeSpan.FromMinutes(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("auth", context =>
    {
        var partitionKey = $"ip:{GetClientIp(context)}";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10, Window = TimeSpan.FromSeconds(60),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
        if (policy is not ("card-media-upload" or "card-media-import"))
            return RateLimitPartition.GetNoLimiter("unlimited");

        var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId != null ? $"user:{userId}" : $"ip:{GetClientIp(context)}";

        // A batch request normalizes up to twenty images, so it gets a tighter concurrency cap than
        // single uploads and its own partition. Requests are mostly sequential-CDN-write idle time,
        // which is why three permits stay cheap on CPU.
        if (policy == "card-media-import")
            return RateLimitPartition.GetConcurrencyLimiter($"import:{partitionKey}",
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 4, QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 4
                });

        return RateLimitPartition.GetConcurrencyLimiter(partitionKey,
            _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 3, QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 5
            });
    });

    options.OnRejected = async (context, token) =>
    {
        var origin = context.HttpContext.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrEmpty(origin))
        {
            if (origin.StartsWith("http://localhost:") ||
                origin.StartsWith("https://localhost:") ||
                origin == "https://jiten.moe" ||
                origin == "https://kizuna-texthooker-ui.fly.dev" ||
                origin == "https://kizuna-texthooker-ui.app")
            {
                context.HttpContext.Response.Headers.Append("Access-Control-Allow-Origin", origin);

                if (!context.HttpContext.Response.Headers.ContainsKey("Access-Control-Expose-Headers"))
                    context.HttpContext.Response.Headers.Append("Access-Control-Expose-Headers", "Retry-After");
            }
        }

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(NumberFormatInfo.InvariantInfo);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "text/plain";
        await context.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", token);
    };
});

builder.Services.AddResponseCaching();
builder.Services.AddMemoryCache();

var allowPrivateNetworkOrigins = builder.Environment.IsDevelopment();

static bool IsPrivateNetworkOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !IPAddress.TryParse(uri.Host, out var ip))
        return false;

    var octets = ip.GetAddressBytes();
    if (octets.Length != 4)
        return false;

    return octets[0] == 10
           || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
           || (octets[0] == 192 && octets[1] == 168);
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigin", policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (origin is null)
                    return false;

                if (origin.StartsWith("http://localhost:") ||
                    origin.StartsWith("https://localhost:"))
                {
                    return true;
                }

                if (allowPrivateNetworkOrigins && IsPrivateNetworkOrigin(origin))
                    return true;

                return origin == "https://jiten.moe" ||
                       origin == "https://kizuna-texthooker-ui.fly.dev" ||
                       origin == "https://kizuna-texthooker-ui.app";
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("Retry-After");
    });
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Hangfire jobs
builder.Services.AddScoped<ParseJob>();
builder.Services.AddScoped<WebNovelImportJob>();
builder.Services.AddScoped<WebNovelFetchJob>();
builder.Services.AddScoped<WebNovelSyncSweepJob>();
builder.Services.AddScoped<ReparseJob>();
builder.Services.AddScoped<ComputationJob>();
builder.Services.AddScoped<SrsRecomputeJob>();
builder.Services.AddScoped<ReviewRollupJob>();
builder.Services.AddScoped<DifficultyAdjustmentJob>();
builder.Services.AddScoped<RecomputeVectorsJob>();
builder.Services.AddScoped<DescriptionEmbeddingJob>();
builder.Services.AddScoped<StripeReconcileJob>();
builder.Services.AddScoped<RenewalReminderJob>();
builder.Services.AddScoped<DecrementPromoCreditsJob>();
builder.Services.AddScoped<FrequencyListJob>();
builder.Services.AddScoped<RoadmapJob>();
builder.Services.AddScoped<CardMediaRenormalizeJob>();

builder.Services.AddHangfire(configuration =>
                                 configuration.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                                              .UseSimpleAssemblyNameTypeSerializer()
                                              .UseRecommendedSerializerSettings()
                                              .UsePostgreSqlStorage((options) =>
                                                                        options.UseNpgsqlConnection(() => builder.Configuration
                                                                            .GetConnectionString("JitenDatabase"))));

// Configure Hangfire global settings for long-running jobs
GlobalJobFilters.Filters.Add(new AutomaticRetryAttribute { Attempts = 3 });

// Hangfire servers
// Fetchers only have 1 worker to respect rate limits
builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "AnilistServer";
    options.Queues = ["anilist"];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "TmdbServer";
    options.Queues = ["tmdb"];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "VndbServer";
    options.Queues = ["vndb"];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "GoogleBooksServer";
    options.Queues = ["books"];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "IgdbServer";
    options.Queues = ["igdb"];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "JikanServer";
    options.Queues = ["jikan"];
    options.WorkerCount = 1;
});


builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "WebNovelSyosetuServer";
    options.Queues = [WebNovelQueues.Syosetu];
    options.WorkerCount = 1;
    // A large novel can take hours
    options.ShutdownTimeout = TimeSpan.FromHours(3);
    options.StopTimeout = TimeSpan.FromHours(3);
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "WebNovelSyosetuMetadataServer";
    options.Queues = [WebNovelQueues.SyosetuMetadata];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "CoverageServer";
    options.Queues = [CoverageQueues.Incremental];
    options.WorkerCount = 4;
});

// Single worker: a full recompute scans all of DeckWords, so concurrent runs thrash the shared
// buffer pool and slow every other query rather than finishing any sooner.
builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "CoverageFullServer";
    options.Queues = [CoverageQueues.Full];
    options.WorkerCount = 1;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "StatsServer";
    options.Queues = ["stats"];
    options.WorkerCount = 5;
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "ParseServer";
    options.Queues = ["parse", "reparse"];
    options.WorkerCount = Math.Max(1, Environment.ProcessorCount / 4);
    options.ShutdownTimeout = TimeSpan.FromMinutes(30);
    options.StopTimeout = TimeSpan.FromMinutes(30);
});

builder.Services.AddHangfireServer((options) =>
{
    options.ServerName = "DefaultServer";
    options.Queues = ["default"];
    options.WorkerCount = Math.Max(1, Environment.ProcessorCount / 4);
    options.ShutdownTimeout = TimeSpan.FromMinutes(30);
    options.StopTimeout = TimeSpan.FromMinutes(30);
});


builder.Services.Configure<FormOptions>(options => { options.ValueCountLimit = 8192; });

var app = builder.Build();

if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var roleName in Enum.GetNames(typeof(UserRole)))
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            await roleManager.CreateAsync(new IdentityRole(roleName));
        }
    }

    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<ComputationJob>(
                                              "updateCoverage",
                                              job => job.DailyUserCoverage(),
                                              Cron.Daily());

    recurringJobs.AddOrUpdate<DifficultyAdjustmentJob>(
        "difficulty-adjustment",
        job => job.ComputeAllAdjustments(),
        "0 */6 * * *");

    recurringJobs.AddOrUpdate<ComputationJob>(
        "coverage-sweep",
        job => job.SweepPendingCoverageDecks(),
        "*/15 * * * *");

    recurringJobs.AddOrUpdate<ReviewRollupJob>(
        "review-rollup-sweep",
        job => job.RebuildDirty(),
        "*/15 * * * *");

    recurringJobs.AddOrUpdate<RecomputeVectorsJob>(
        "recompute-deck-vectors",
        job => job.Recompute(),
        Cron.Daily(4));

    recurringJobs.AddOrUpdate<RecomputeVectorsJob>(
        "embed-pending-decks",
        job => job.EmbedPending(),
        "*/30 * * * *");

    recurringJobs.AddOrUpdate<DescriptionEmbeddingJob>(
        "sync-description-embeddings",
        job => job.Sync(),
        Cron.Hourly(20));

    recurringJobs.AddOrUpdate<WebNovelSyncSweepJob>(
        "webnovel-sync-sweep",
        job => job.Sweep(),
        Cron.Daily(5));

    recurringJobs.AddOrUpdate<StripeReconcileJob>(
        "stripe-reconcile",
        job => job.Reconcile(),
        Cron.Daily(6));

    recurringJobs.AddOrUpdate<RenewalReminderJob>(
        "renewal-reminder",
        job => job.Run(),
        Cron.Daily(7));

    recurringJobs.AddOrUpdate<DecrementPromoCreditsJob>(
        "promo-credits-decrement",
        job => job.Run(),
        Cron.Daily(1));

    // Auto-update lists are regenerated at the end of ComputationJob.RecomputeFrequencies instead of on a schedule.
    recurringJobs.RemoveIfExists("freq-list-auto-update");

    recurringJobs.AddOrUpdate<FrequencyListJob>(
        "freq-list-transient-cleanup",
        job => job.CleanupTransientLists(),
        Cron.Daily(3));

    recurringJobs.AddOrUpdate<SequenceMonitorJob>(
        "sequence-monitor",
        job => job.CheckSequences(),
        Cron.Daily(5));
}

app.UseResponseCompression();

app.UseForwardedHeaders(new ForwardedHeadersOptions
                        {
                            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                            KnownNetworks = { new IPNetwork(IPAddress.Parse("10.0.0.0"), 8) },
                            KnownProxies = { IPAddress.Parse("10.0.4.2") }, RequireHeaderSymmetry = false, ForwardLimit = 1
                        });

// Security headers middleware
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API V1");
    c.RoutePrefix = "";
});

app.UseRouting();

app.UseCors("AllowSpecificOrigin");

app.UseResponseCaching();

app.UseAuthentication();

// Rate limiting is IP-partitioned; the integration test suite drives many auth-policy endpoints from a
// single loopback IP, so skip the limiter under the Testing environment (matches other Testing guards).
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseRateLimiter();
}

app.UseStaticFiles();

bool.TryParse(app.Configuration["UseBunnyCdn"], out var useBunnyCdn);
if (useBunnyCdn)
{
    //
}
else
{
    app.UseStaticFiles(new StaticFileOptions
                       {
                           FileProvider =
                               new PhysicalFileProvider(app.Configuration["StaticFilesPath"] ??
                                                        throw new Exception("Please set the StaticFilesPath in appsettings.json")),
                           RequestPath = "/static"
                       });
}

if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions() { Authorization = [new HangfireAuthorizationFilter(app.Configuration)] });
    app.MapHangfireDashboard();
}

app.MapSwagger();
if (enableOtlpExporter)
{
    app.UseRequestLogging();
}

app.UseAuthorization();
app.MapControllers();
var gateHealthOnWarmup = !app.Environment.IsEnvironment("Testing");
app.MapGet("/health", (StartupReadiness readiness) =>
    !gateHealthOnWarmup || readiness.IsReady
        ? Results.Ok("healthy")
        : Results.Json(new { status = "warming", pending = readiness.Pending }, statusCode: StatusCodes.Status503ServiceUnavailable));

app.Run();

static string GetClientIp(HttpContext context)
{
    foreach (var header in Program._proxyHeaders)
    {
        var value = context.Request.Headers[header].FirstOrDefault();
        if (!string.IsNullOrEmpty(value))
        {
            // X-Forwarded-For can be comma-separated, take the first (original client)
            var ip = value.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip) && ip != "unknown")
            {
                return ip;
            }
        }
    }

    // Fallback to connection IP (will be Traefik's IP)
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public partial class Program
{
    private static readonly string[] _proxyHeaders =
        ["X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP"];
}
