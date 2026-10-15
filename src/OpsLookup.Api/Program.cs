using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpsLookup.Api.Data;
using OpsLookup.Api.Endpoints;
using OpsLookup.Api.OpenApi;
using OpsLookup.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------- configuration
var jwtOptions = builder.Configuration
    .GetSection(JwtOptions.SectionName)
    .Get<JwtOptions>() ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

if (builder.Environment.IsProduction() &&
    (string.IsNullOrWhiteSpace(jwtOptions.SigningKey) || jwtOptions.SigningKey.Length < 32))
{
    throw new InvalidOperationException(
        "Jwt:SigningKey must be set to at least 32 characters in Production.");
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));

// ------------------------------------------------------------------- services
builder.Services.AddDbContext<ContractDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("ContractDb"),
        sql => sql.EnableRetryOnFailure()));

builder.Services.AddScoped<IContractRepository, ContractRepository>();
builder.Services.AddScoped<IAttorneyRepository, AttorneyRepository>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddOpenApi("v1", SwaggerConfiguration.AddOpsLookupDocument);

var app = builder.Build();

// --------------------------------------------------------------------- pipeline
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    app.Logger.LogError(feature?.Error, "Unhandled exception");

    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    context.Response.ContentType = "application/problem+json";

    await context.Response.WriteAsJsonAsync(new
    {
        title = "Unexpected error",
        status = StatusCodes.Status500InternalServerError,
    });
}));

app.UseAuthentication();
app.UseAuthorization();

// ------------------------------------------------------------------- OpenAPI
app.MapOpenApi();

app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "OpsLookup API v1");
    options.RoutePrefix = "swagger";
    options.DocumentTitle = "OpsLookup API";
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .AllowAnonymous();

app.MapContractEndpoints();
app.MapAttorneyEndpoints();

// Service tokens are how the agent host authenticates in every environment.
app.MapServiceTokenEndpoints();

// The dev-token endpoint is anonymous and lets the caller choose both the
// subject and the lifetime, so it must never exist outside Development. It was
// previously mapped unconditionally: on the IIS deployment anyone who could
// reach the port could mint a year-long token and read every contract and
// attorney row.
if (app.Environment.IsDevelopment())
{
    app.MapDevTokenEndpoints();
}

if (app.Environment.IsDevelopment())
{
    

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ContractDbContext>();
        await db.Database.MigrateAsync();
        await DevSeeder.SeedAsync(db);
    }
}

app.Run();

/// <summary>Exposed so integration tests can reference the entry-point assembly.</summary>
public partial class Program;