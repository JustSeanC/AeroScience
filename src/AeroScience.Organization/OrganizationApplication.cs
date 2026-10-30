using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AeroScience.Shared;
using AeroScience.Missions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;

namespace AeroScience.Organization;

public sealed class OrganizationProgram
{
    public static async Task Main(string[] args)
    {
        if(args is ["--apply-prepared-update"]){
            try{await using var updateApp=OrganizationApplication.Build([]);var id=updateApp.Configuration["Organization:Operations:ApplyOperationId"];if(!Guid.TryParse(id,out var operation))throw new InvalidOperationException("An exact protected approved operation is required.");await OrganizationSchemaUpgrade.ApplyPrepared(updateApp.Services,operation,CancellationToken.None);Console.WriteLine("Exact approved schema update completed. No public listener or provider rollout was started.");}
            catch(Exception){Console.Error.WriteLine("Protected update refused or rolled back. Preserve the original image, identity and verified checkpoint; no credential values are disclosed.");Environment.ExitCode=1;}return;
        }
        if(args is ["--health-check"]){
            try{using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(3)};using var response=await client.GetAsync("http://127.0.0.1:8080/healthz/ready");Environment.ExitCode=response.IsSuccessStatusCode?0:1;}catch{Environment.ExitCode=1;}return;
        }
        try{if(args is ["--railway-volume-check"]){RailwayRuntime.InitializeVolumeAndDropPrivileges();RailwayRuntime.RefuseRootApplication();Console.WriteLine("Restricted service identity ready; no network listener started.");return;}if(args is ["--railway-start"]){RailwayRuntime.InitializeVolumeAndDropPrivileges();var configuration=new ConfigurationManager();configuration.AddEnvironmentVariables();await RailwayRuntime.Configure(configuration,CancellationToken.None);var railwayApp=OrganizationApplication.Build([],b=>b.Configuration.AddConfiguration(configuration));await OrganizationApplication.Initialize(railwayApp);await railwayApp.RunAsync();return;}var app=OrganizationApplication.Build(args);await OrganizationApplication.Initialize(app);await app.RunAsync();}
        catch(Exception ex){Console.Error.WriteLine($"Organization startup stopped safely ({ex.GetType().Name}). Check protected deployment configuration and database/schema compatibility. No credential values are printed.");Environment.ExitCode=1;}
    }
}
public sealed record OrganizationBootstrapRequest(string Secret,string OrganizationName,string Description,string UserName,string Password);
public sealed record OrganizationLoginRequest(string UserName,string Password);
public sealed class OrganizationDatabaseEncodingException():InvalidOperationException("Organization PostgreSQL must use UTF8 to retain multilingual research names, objectives and provenance. Startup stopped before migration; existing records were not converted or changed.");
public sealed class BootstrapCredential:IDisposable
{
    byte[]? digest;DateTimeOffset expires=DateTimeOffset.MaxValue;
    public void Initialize(byte[] secret,DateTimeOffset? expiresUtc=null){expires=expiresUtc??DateTimeOffset.MaxValue;digest=SHA256.HashData(secret);CryptographicOperations.ZeroMemory(secret);}
    public bool Matches(string? input)=>digest is not null&&DateTimeOffset.UtcNow<expires&&input is {Length:>=32 and <=256}&&CryptographicOperations.FixedTimeEquals(digest,SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    public void Dispose(){if(digest is not null)CryptographicOperations.ZeroMemory(digest);digest=null;}
}
public static class OrganizationApplication
{
    static readonly SemaphoreSlim ReadinessLimit=new(2,2);
    public const string ManagePolicy="OrganizationManagement",PublishPolicy="OrganizationAdministration";
    public static WebApplication Build(string[] args,Action<WebApplicationBuilder>? configure=null)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{Args=args,ApplicationName=typeof(OrganizationProgram).Assembly.FullName});
        configure?.Invoke(builder);
        var settings=OrganizationSettings.Read(builder.Configuration,builder.Environment);
        if(!settings.LocalDevelopment)RailwayRuntime.RefuseRootApplication();
        OrganizationDeployment.PrepareCertificate(settings);
        if(settings.LocalDevelopment)builder.WebHost.UseUrls(settings.Origin.GetLeftPart(UriPartial.Authority));
        builder.WebHost.ConfigureKestrel(o=>{o.Limits.MaxRequestBodySize=32768;o.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);});
        builder.Services.ConfigureHttpJsonOptions(o=>{
            o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase;o.SerializerOptions.MaxDepth=24;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase,allowIntegerValues:false));
            o.SerializerOptions.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow;
        });
        builder.Services.Configure<HostOptions>(o=>o.ShutdownTimeout=TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton(ServerOperationsOptions.Read(builder.Configuration,settings));
        builder.Services.AddScoped<OrganizationOperations>();builder.Services.AddScoped<OrganizationRecoveryEngine>();builder.Services.AddHostedService<OrganizationOperationsWorker>();
        builder.Services.AddSingleton(settings);builder.Services.AddSingleton<BootstrapCredential>();
        builder.Services.AddDbContext<OrganizationDatabase>(o=>o.UseNpgsql(settings.ConnectionString,n=>n.CommandTimeout(15)));
        var keys=builder.Services.AddDataProtection().SetApplicationName("AeroScience.Organization.0.7").PersistKeysToDbContext<OrganizationDatabase>();
        var certificate=settings.LoadKeyCertificate();if(certificate is not null)keys.ProtectKeysWithCertificate(certificate);
        builder.Services.AddIdentityCore<OrganizationUser>(o=>{
            o.Password.RequiredLength=14;o.Password.RequireDigit=true;o.Password.RequireUppercase=true;o.Password.RequireLowercase=true;o.Password.RequireNonAlphanumeric=true;
            o.Lockout.MaxFailedAccessAttempts=5;o.Lockout.DefaultLockoutTimeSpan=TimeSpan.FromMinutes(15);o.Lockout.AllowedForNewUsers=true;
        }).AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<OrganizationDatabase>().AddSignInManager().AddDefaultTokenProviders();
        builder.Services.AddScoped<IUserStore<OrganizationUser>,ProtectedOrganizationUserStore>();
        builder.Services.AddScoped<OrganizationAccountSecurity>();
        builder.Services.Configure<PasswordHasherOptions>(o=>o.IterationCount=210000);
        builder.Services.Configure<SecurityStampValidatorOptions>(o=>{o.ValidationInterval=TimeSpan.Zero;o.OnRefreshingPrincipal=c=>{var proof=c.CurrentPrincipal?.FindFirst(OrganizationAccountSecurity.MfaClaim);if(proof is not null&&c.NewPrincipal?.Identity is ClaimsIdentity identity)identity.AddClaim(proof);return Task.CompletedTask;};});
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        builder.Services.ConfigureApplicationCookie(o=>{
            o.Cookie.Name=settings.LocalDevelopment?"AeroScience.Organization.Development":"__Host-AeroScience.Organization";
            o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.Path="/";o.Cookie.SecurePolicy=settings.LocalDevelopment?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
            o.ExpireTimeSpan=TimeSpan.FromMinutes(30);o.SlidingExpiration=true;
            o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};
            o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};
            o.Events.OnSigningIn=c=>{c.Properties.Items["organization-started"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);return Task.CompletedTask;};
            o.Events.OnValidatePrincipal=async c=>{
                if(!c.Properties.Items.TryGetValue("organization-started",out var value)||!long.TryParse(value,out var issued)||DateTimeOffset.UtcNow.ToUnixTimeSeconds()-issued>28800){c.RejectPrincipal();await c.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);return;}
                await SecurityStampValidator.ValidatePrincipalAsync(c);
            };
        });
        foreach(var scheme in new[]{IdentityConstants.ExternalScheme,IdentityConstants.TwoFactorUserIdScheme,IdentityConstants.TwoFactorRememberMeScheme})
            builder.Services.Configure<CookieAuthenticationOptions>(scheme,o=>{o.Cookie.Name=(settings.LocalDevelopment?"AeroScience.Organization.":"__Host-AeroScience.Organization.")+scheme.Replace("Identity.","");o.Cookie.Path="/";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=settings.LocalDevelopment?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;});
        builder.Services.AddAuthorization(o=>{
            o.AddPolicy(ManagePolicy,p=>p.RequireAuthenticatedUser().AddRequirements(new ActiveOrganizationRole(false)));
            o.AddPolicy(PublishPolicy,p=>p.RequireAuthenticatedUser().AddRequirements(new ActiveOrganizationRole(true)));
        });
        builder.Services.AddScoped<IAuthorizationHandler,ActiveOrganizationRoleHandler>();
        builder.Services.AddAntiforgery(o=>{
            o.HeaderName="X-AeroScience-Organization-CSRF";o.Cookie.Name=settings.LocalDevelopment?"AeroScience.Organization.Antiforgery.Development":"__Host-AeroScience.Organization.Antiforgery";
            o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;o.Cookie.SecurePolicy=settings.LocalDevelopment?CookieSecurePolicy.SameAsRequest:CookieSecurePolicy.Always;
        });
        builder.Services.AddRateLimiter(o=>{
            o.RejectionStatusCode=429;
            o.AddPolicy("identity",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local-test",_=>new(){PermitLimit=12,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.AddPolicy("public",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local-test",_=>new(){PermitLimit=60,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.AddPolicy("device-poll",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local-test",_=>new(){PermitLimit=20,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.AddPolicy("evidence-receive",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"local-test",_=>new(){PermitLimit=6,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.AddPolicy("live-read",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"live",_=>new(){PermitLimit=600,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.AddPolicy("live-write",c=>RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString()??"live",_=>new(){PermitLimit=1000,Window=TimeSpan.FromMinutes(1),QueueLimit=0,AutoReplenishment=true}));
            o.GlobalLimiter=PartitionedRateLimiter.Create<HttpContext,string>(_=>RateLimitPartition.GetConcurrencyLimiter("organization",_=>new(){PermitLimit=32,QueueLimit=0}));
        });
        builder.Services.TryAddSingleton(TimeProvider.System);builder.Services.AddSingleton<OrganizationLiveRuntime>();builder.Services.AddScoped<OrganizationLiveFlights>();builder.Services.AddScoped<OrganizationEvents>();builder.Services.AddHostedService<OrganizationLiveWorker>();
        builder.Services.AddScoped<OrganizationServerDiagnostics>();
        builder.Services.AddScoped<OrganizationManagement>();
        builder.Services.AddScoped<OrganizationPublishing>();builder.Services.AddScoped<OrganizationResearchTemplates>();
        builder.Services.AddScoped<OrganizationOpportunities>();builder.Services.AddScoped<OrganizationMembership>();builder.Services.AddScoped<OrganizationDevices>();
        builder.Services.AddScoped<ProgramWorkflow>();builder.Services.AddScoped<CampaignCoverageProjection>();builder.Services.AddScoped<CampaignProgressProjection>();builder.Services.AddScoped<OrganizationCampaignProgress>();builder.Services.AddScoped<OrganizationSubmissions>();builder.Services.AddScoped<OrganizationReviews>();
        var submissionLimit=builder.Configuration.GetValue<long?>("Organization:MaximumRetainedEvidenceBytes")??1024L*1024*1024;
        if(submissionLimit<SubmissionEvidenceProtocol.MaximumPayloadBytes||submissionLimit>1024L*1024*1024*1024)throw new InvalidOperationException("Configure a bounded organization evidence-storage quota between 2 MiB and 1 TiB.");
        builder.Services.AddSingleton(new OrganizationSubmissionOptions{MaximumRetainedPayloadBytes=submissionLimit});
        builder.Logging.AddFilter("Microsoft.AspNetCore",LogLevel.Warning); // Never log invitation/connection input or credentials.
        var app=builder.Build();
        if(certificate is not null)app.Lifetime.ApplicationStopped.Register(certificate.Dispose);
        if(settings.TrustedProxies.Length>0){var forwarded=new ForwardedHeadersOptions{ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto,ForwardLimit=1};forwarded.KnownIPNetworks.Clear();forwarded.KnownProxies.Clear();foreach(var proxy in settings.TrustedProxies)forwarded.KnownProxies.Add(proxy);app.UseForwardedHeaders(forwarded);}
        app.Use(async(context,next)=>{
            context.Response.Headers.CacheControl="no-store";context.Response.Headers.XContentTypeOptions="nosniff";context.Response.Headers["Referrer-Policy"]="no-referrer";
            context.Response.Headers.ContentSecurityPolicy="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            if(!settings.LocalDevelopment)context.Response.Headers.StrictTransportSecurity="max-age=31536000";
            var authority=settings.Origin.GetLeftPart(UriPartial.Authority);
            // Only an in-container loopback probe bypasses public origin matching, and returns no details.
            if(context.Request.Path=="/healthz/ready"&&context.Request.Method=="GET"&&((context.Connection.RemoteIpAddress is {} probe&&IPAddress.IsLoopback(probe)&&context.Request.Host.Host=="127.0.0.1")||(settings.HostingProvider=="railway"&&context.Request.Host.Host=="healthcheck.railway.app"))){
                if(!await ReadinessLimit.WaitAsync(0,context.RequestAborted)){context.Response.StatusCode=503;return;}
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try{using var scope=app.Services.CreateScope();var database=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();context.Response.StatusCode=await database.Database.CanConnectAsync(deadline.Token)?200:503;}
                catch{context.Response.StatusCode=503;}finally{ReadinessLimit.Release();}return;
            }
            if(!string.Equals(context.Request.Scheme+"://"+context.Request.Host,authority,StringComparison.OrdinalIgnoreCase)
                ||settings.LocalDevelopment&&context.Connection.RemoteIpAddress is {} remote&&!IPAddress.IsLoopback(remote)){
                context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{error="Open the configured organization server address using its approved secure connection."});return;
            }
            var evidence=context.Request.Method=="POST"&&context.Request.Path==OrganizationSubmissionApi.ReceivePath;
            var bodyLimit=evidence?SubmissionEvidenceProtocol.MaximumWireBytes:32768;
            if(evidence&&context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} feature)feature.MaxRequestBodySize=bodyLimit;
            if(context.Request.ContentLength>bodyLimit){context.Response.StatusCode=413;await context.Response.WriteAsJsonAsync(new{error="The organization request is too large."});return;}
            try{await next();}
            catch(Exception ex) when(ex is ArgumentException or BadHttpRequestException or JsonException or OrganizationConflict or OrganizationNotFound){
                if(context.Response.HasStarted){context.Abort();return;}
                context.Response.StatusCode=ex is OrganizationConflict?409:ex is OrganizationNotFound?404:ex is BadHttpRequestException bad&&bad.StatusCode==413?413:400;
                await context.Response.WriteAsJsonAsync(new{error=ex is OrganizationNotFound?"This organization record was not found.":ex is BadHttpRequestException or JsonException?"The request is invalid or contains unsupported fields.":ex.Message});
            }
            catch(OperationCanceledException) when(context.RequestAborted.IsCancellationRequested){ }
            catch(OrganizationForbidden){context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{error="This account or membership is not authorized for that action."});}
            catch(AccountSecurityDenied ex){context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new{error=ex.Message});}
            catch(OrganizationUnauthorized){context.Response.StatusCode=401;await context.Response.WriteAsJsonAsync(new{error="This organization connection is expired or revoked. Connect again to continue."});}
            catch(Exception ex){if(context.Response.HasStarted){context.Abort();return;}app.Logger.LogError("Organization operation unavailable ({ExceptionType}); no request bodies or credentials are logged.",ex.GetType().Name);context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{error="Organization storage or services are unavailable. No change was confirmed; keep your draft and try again."});}
        });
        app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();
        app.Use(async(context,next)=>{
            if(context.Request.Method is not("GET" or "HEAD" or "OPTIONS")){
                // Native device client only. Browser actions retain Origin + antiforgery.
                if(context.Request.Path is var path&&(path=="/api/device/start"||path=="/api/device/poll"||path=="/api/device/disconnect"||path=="/api/member/claim-command"||path==OrganizationSubmissionApi.ReceivePath||path==OrganizationSubmissionApi.CancelPath||OrganizationReviewApi.IsNativeResponse(path)||OrganizationLiveApi.IsNativeWrite(path))){
                    if(context.Request.Headers.ContainsKey("Origin")||context.Request.Headers.ContainsKey("Cookie")){context.Response.StatusCode=403;return;}
                    await next();return;
                }
                if(context.Request.Headers.Origin.ToString()!=settings.Origin.GetLeftPart(UriPartial.Authority)){context.Response.StatusCode=403;return;}
                try{await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);}
                catch(AntiforgeryValidationException){context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{error="This page's security session changed. Refresh the page before trying again."});return;}
            }
            await next();
        });
        app.Use(async(context,next)=>{
            if(settings.RequireAdministratorMfa&&context.User.Identity?.IsAuthenticated==true&&context.User.IsInRole("Administrator")&&context.Request.Path.StartsWithSegments("/api")
                &&context.Request.Path!="/api/session"&&context.Request.Path!="/api/sign-out"&&!context.Request.Path.StartsWithSegments("/api/account-security")){
                var user=await context.RequestServices.GetRequiredService<UserManager<OrganizationUser>>().GetUserAsync(context.User);
                if(user is null||!OrganizationAccountSecurity.Ready(user)||!OrganizationAccountSecurity.Verified(context.User)){context.Response.StatusCode=428;await context.Response.WriteAsJsonAsync(new{error="Complete Secure Your Organization, including an authenticator and saved recovery codes, before administering this server.",accountSecurityRequired=true});return;}
            }
            await next();
        });
        var assets=Path.Combine(AppContext.BaseDirectory,"organization-portal");
        if(Directory.Exists(assets)){var files=new PhysicalFileProvider(assets);app.UseDefaultFiles(new DefaultFilesOptions{FileProvider=files});app.UseStaticFiles(new StaticFileOptions{FileProvider=files});}
        MapIdentity(app);OrganizationLiveApi.Map(app);OrganizationAccountSecurityApi.Map(app);OrganizationOperationsApi.Map(app);MapManagement(app);ResearchTemplateApi.Map(app);OrganizationEnrollmentApi.Map(app);OrganizationOpportunityApi.Map(app);OrganizationSubmissionApi.Map(app);OrganizationReviewApi.Map(app);CampaignProgressApi.Map(app);CampaignCoverageApi.Map(app);ProgramWorkflowApi.Map(app);return app;
    }
    public static async Task Initialize(WebApplication app,CancellationToken token=default)
    {
        using var scope=app.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<OrganizationDatabase>();
        try{
            if(await db.Database.SqlQueryRaw<string>("SELECT current_setting('server_encoding') AS \"Value\"").SingleAsync(token)!="UTF8")throw new OrganizationDatabaseEncodingException();
            var known=db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            if((await db.Database.GetAppliedMigrationsAsync(token)).Any(m=>!known.Contains(m)))throw new InvalidOperationException("Organization database schema is newer or incompatible.");
            var deploymentSettings=app.Services.GetRequiredService<OrganizationSettings>();
            if(!deploymentSettings.LocalDevelopment&&(await db.Database.GetAppliedMigrationsAsync(token)).Any()&&(await db.Database.GetPendingMigrationsAsync(token)).Any())
                throw new InvalidOperationException("Existing production data require an accepted paired-recovery upgrade. Automatic production migration is blocked; no previous image alone can roll back a schema.");
            await db.Database.MigrateAsync(token);scope.ServiceProvider.GetRequiredService<ProgramWorkflow>().Rebuilding=true;await scope.ServiceProvider.GetRequiredService<OrganizationReviews>().Backfill(token);await scope.ServiceProvider.GetRequiredService<CampaignProgressProjection>().Backfill(token);await scope.ServiceProvider.GetRequiredService<CampaignCoverageProjection>().Backfill(token);await scope.ServiceProvider.GetRequiredService<ProgramWorkflow>().Backfill(token);var settings=app.Services.GetRequiredService<OrganizationSettings>();
            await OrganizationDeployment.Initialize(db,settings,app.Services.GetRequiredService<BootstrapCredential>(),token);
            if(!settings.ManagedDeployment&&!await db.Organizations.AnyAsync(token))app.Services.GetRequiredService<BootstrapCredential>().Initialize(settings.ReadBootstrapSecret());
        }
        catch(OrganizationDatabaseEncodingException){throw;}
        catch(Exception ex) when(ex is not OperationCanceledException){throw new InvalidOperationException("Organization startup refused: database/schema, protected bootstrap or key configuration is unavailable. No personal-data fallback was used.");}
    }
    static void MapManagement(WebApplication app)
    {
        app.MapGet("/api/health",()=>new{service="AeroScience Organization",version="0.7.0",schema=1});
        var api=app.MapGroup("/api/organization").RequireAuthorization(ManagePolicy);
        api.MapGet("/server-health",(OrganizationServerDiagnostics diagnostics,CancellationToken token)=>diagnostics.Check(token)).RequireAuthorization(PublishPolicy);
        api.MapGet("/overview",async(OrganizationDatabase db,CancellationToken token)=>{
            var organization=await db.Organizations.AsNoTracking().SingleAsync(token);return new{version=1,id=organization.PublicId,organization.Name,organization.Description,organization.CreatedUtc};
        });
        api.MapGet("/templates",()=>OrganizationTemplates.All());
        api.MapGet("/programs",(int? offset,OrganizationManagement management,CancellationToken token)=>management.Programs(offset??0,token));
        api.MapGet("/programs/{id:guid}",(Guid id,OrganizationManagement management,CancellationToken token)=>management.Program(id,token));
        api.MapPost("/programs",(OrganizationProgramRequest request,OrganizationManagement management,HttpContext c)=>management.SaveProgram(null,request,c.User,c.RequestAborted));
        api.MapPut("/programs/{id:guid}",(Guid id,OrganizationProgramRequest request,OrganizationManagement management,HttpContext c)=>management.SaveProgram(id,request,c.User,c.RequestAborted));
        api.MapGet("/programs/{program:guid}/campaigns",(Guid program,int? offset,OrganizationManagement management,CancellationToken token)=>management.Campaigns(program,offset??0,token));
        api.MapPost("/programs/{program:guid}/campaigns",(Guid program,OrganizationCampaignRequest request,OrganizationManagement management,HttpContext c)=>management.SaveCampaign(program,null,request,c.User,c.RequestAborted));
        api.MapPut("/programs/{program:guid}/campaigns/{id:guid}",(Guid program,Guid id,OrganizationCampaignRequest request,OrganizationManagement management,HttpContext c)=>management.SaveCampaign(program,id,request,c.User,c.RequestAborted));
        // The service enforces the explicit legacy default or the administrator-selected policy.
        api.MapPost("/campaigns/{id:guid}/publish",(Guid id,OrganizationPublishRequest request,OrganizationManagement management,HttpContext c)=>management.Publish(id,request,c.User,c.RequestAborted));
        api.MapGet("/definitions/{id:guid}/revisions/{revision:int}",(Guid id,int revision,OrganizationManagement management,CancellationToken token)=>management.ReadRevision(id,revision,token));
        api.MapGet("/audit",async(long? after,OrganizationDatabase db,CancellationToken token)=>{
            if(after<0)throw new ArgumentException("Choose a valid audit page.");return await db.Audit.AsNoTracking().Where(a=>a.Id>(after??0)).OrderBy(a=>a.Id).Take(100).ToArrayAsync(token);
        }).RequireAuthorization(PublishPolicy);
    }
    static OrganizationSettings settingsFor(HttpContext context)=>context.RequestServices.GetRequiredService<OrganizationSettings>();
    static void MapIdentity(WebApplication app)
    {
        app.MapGet("/api/session",async(HttpContext c,OrganizationDatabase db,IAntiforgery csrf)=>new{
            membershipState=c.User.Identity?.IsAuthenticated==true?await db.Memberships.Where(m=>m.UserId==Guid.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!)).Select(m=>(OrganizationMembershipState?)m.State).SingleOrDefaultAsync(c.RequestAborted):null,
            initialized=await db.Organizations.AnyAsync(c.RequestAborted),csrf=csrf.GetAndStoreTokens(c).RequestToken,
            accountSecurityRequired=settingsFor(c).RequireAdministratorMfa&&c.User.IsInRole("Administrator")&&(!OrganizationAccountSecurity.Verified(c.User)||!OrganizationAccountSecurity.Ready((await db.Users.SingleAsync(u=>u.Id==Guid.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!),c.RequestAborted)))),
            user=c.User.Identity?.IsAuthenticated==true?new{id=c.User.FindFirstValue(ClaimTypes.NameIdentifier),name=c.User.Identity.Name,roles=c.User.FindAll(ClaimTypes.Role).Select(r=>r.Value).ToArray()}:null
        });
        app.MapPost("/api/bootstrap",async(OrganizationBootstrapRequest request,BootstrapCredential credential,OrganizationDatabase db,UserManager<OrganizationUser> users,RoleManager<IdentityRole<Guid>> roles,OrganizationManagement management,HttpContext c)=>{
            if(!credential.Matches(request.Secret))return Results.Json(new{error="The protected organization setup credential was not accepted."},statusCode:403);
            ValidateIdentity(request.UserName,request.Password);
            if(string.IsNullOrWhiteSpace(request.OrganizationName)||request.OrganizationName.Length>120||request.OrganizationName.Any(char.IsControl)||request.Description is null||request.Description.Length>4000||request.Description.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))throw new ArgumentException("Enter an organization name (up to 120 characters) and description (up to 4,000).");
            await using var tx=await db.Database.BeginTransactionAsync(c.RequestAborted);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7013001)",c.RequestAborted);
            if(await db.Organizations.AnyAsync(c.RequestAborted))return Results.Conflict(new{error="Organization setup has already been completed. Sign in with the administrator account."});
            await OrganizationDeployment.ConsumeBootstrap(db,c.RequestServices.GetRequiredService<OrganizationSettings>(),request.Secret,c.RequestAborted);
            foreach(var role in OrganizationRoles.All){var made=await roles.CreateAsync(new(role){Id=Guid.NewGuid()});if(!made.Succeeded)throw new InvalidOperationException("Organization role setup could not be completed.");}
            var admin=new OrganizationUser{Id=Guid.NewGuid(),UserName=request.UserName,SecurityStamp=Guid.NewGuid().ToString()};
            var created=await users.CreateAsync(admin,request.Password);if(!created.Succeeded)throw new ArgumentException("Choose a valid account name and a password with at least 14 characters, upper/lowercase letters, a number and a symbol.");
            var assigned=await users.AddToRoleAsync(admin,OrganizationRoles.Administrator);if(!assigned.Succeeded)throw new InvalidOperationException("Administrator setup could not be completed.");
            var organization=new OrganizationRecord{PublicId=Guid.NewGuid(),Name=request.OrganizationName.Trim(),Description=request.Description.Trim(),AdministratorId=admin.Id,CreatedUtc=OrganizationTime.UtcNow};
            db.Organizations.Add(organization);management.Revision(organization.PublicId,1,"enrollment-policy",new OrganizationEnrollmentPolicy(1,1,OrganizationEnrollmentMode.InvitationOnly,true),admin.Id);db.Memberships.Add(new(){UserId=admin.Id,Role=OrganizationRoles.Administrator,State=OrganizationMembershipState.Active,UpdatedUtc=OrganizationTime.UtcNow});management.Audit(admin.Id,admin.UserName!,"organization-created",organization.PublicId);await db.SaveChangesAsync(c.RequestAborted);await tx.CommitAsync(c.RequestAborted);
            return Results.Ok(new{message="Organization and administrator saved. Sign in to open your research workspace."});
        }).RequireRateLimiting("identity");
        app.MapPost("/api/sign-in",async(OrganizationLoginRequest request,SignInManager<OrganizationUser> signIn,OrganizationDatabase db,OrganizationManagement management)=>{
            ValidateIdentity(request.UserName,request.Password,false);
            var user=await signIn.UserManager.FindByNameAsync(request.UserName);
            await signIn.Context.SignOutAsync(IdentityConstants.ApplicationScheme);
            var result=user is null?SignInResult.Failed:await signIn.PasswordSignInAsync(user,request.Password,isPersistent:false,lockoutOnFailure:true);
            if(result.RequiresTwoFactor)return Results.Ok(new{twoFactorRequired=true,message="Enter your authenticator code or one saved recovery code."});
            if(!result.Succeeded)return Results.Json(new{error="Sign-in was not accepted. Check your account details or wait before trying again."},statusCode:401);
            management.Audit(user!.Id,user.UserName!,"signed-in",user.Id);await db.SaveChangesAsync();return Results.Ok(new{message="Signed in."});
        }).RequireRateLimiting("identity");
        app.MapPost("/api/sign-out",async(SignInManager<OrganizationUser> signIn,OrganizationManagement management,OrganizationDatabase db,HttpContext c)=>{
            var id=Guid.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);management.Audit(id,c.User.Identity!.Name!,"signed-out",id);var user=await signIn.UserManager.GetUserAsync(c.User)??throw new InvalidOperationException("Account unavailable.");var revoked=await signIn.UserManager.UpdateSecurityStampAsync(user);if(!revoked.Succeeded)throw new InvalidOperationException("Session revocation was not confirmed.");await db.DeviceGrants.Where(g=>g.UserId==id).ExecuteUpdateAsync(s=>s.SetProperty(g=>g.Revoked,true),c.RequestAborted);await db.SaveChangesAsync(c.RequestAborted);await signIn.SignOutAsync();return Results.Ok(new{message="Signed out. Organization sessions and desktop connections revoked."});
        }).RequireAuthorization();
    }
    internal static void ValidateIdentity(string? name,string? password,bool creating=true)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>64||name.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not('.' or '_' or '-' or '@'))||password is null||password.Length>(creating?256:256)||password.Length<(creating?14:1))
            throw new ArgumentException("Enter a valid account name and password. New passwords need at least 14 characters, upper/lowercase letters, a number and a symbol.");
    }
}
