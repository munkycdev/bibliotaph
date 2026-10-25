using Bibliotaph.Server;
using Bibliotaph.Server.Accounts;

// "invite [--admin] [--note <text>]" prints a one-time invitation link and exits. It's how the first admin gets in:
// infra/invite.ps1 runs it inside the container with az containerapp exec.
var invite = args is ["invite", ..] ? InviteCommand.Parse(args[1..]) : null;
var builder = WebApplication.CreateBuilder(invite is null ? args : []);

builder.Services.AddBibliotaphServer(builder.Configuration, builder.Environment);
var app = builder.Build();

await app.Services.GetRequiredService<AccountsDatabase>().MigrateAsync();
if (invite is not null) return await invite.RunAsync(app.Services);

app.UseBibliotaphServer();
await app.RunAsync();
return 0;
