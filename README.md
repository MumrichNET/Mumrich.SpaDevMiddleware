# Mumrich.SpaDevMiddleware

A .NET Middleware for **ASP.NET Core 7** that automatically integrates (multiple) Single-Page-Apps in a .NET-Webhost.

- Automatic **node-package install** (`npm`/`yarn`/`pnpm`)
- SPAs are automatically built upon (`dotnet publish` triggers the `build`-script in `package.json`)
- Automatic **path-mapping** for the **SPA** by aid of the superb [YARP](https://microsoft.github.io/reverse-proxy/)
- SPA **Hot-Reloading** supported
- **Custom-ENV-Variables** can be passed to the Node-Instance via `appsettings.json`
- Usage of **MSBUILD-Variables** supported

## Usage

1. Install the `Mumrich.SpaDevMiddleware` Nuget-Package into your Web-Project (.NET 6)

2. Implement the `ISpaDevServerSettings`-interface:

   ```csharp
   public class AppSettings : ISpaDevServerSettings
   {
     public Dictionary<string, SpaSettings> SinglePageApps { get; set; } = new();
     public string SpaRootPath { get; set; } = Directory.GetCurrentDirectory();
     public bool UseParentObserverServiceOnWindows { get; set; } = true;
   }
   ```

3. Extend `appsettings.json` with a SPA-Config e.g.:

   ```json
   {
     "Logging": {
       "LogLevel": {
         "Default": "Information",
         "Microsoft.AspNetCore": "Warning"
       }
     },
     "AllowedHosts": "*",
     "SinglePageApps": {
       "/": {
         "DevServerAddress": "http://localhost:3000/",
         "SpaRootPath": "app1",
         "NodePackageManager": "Yarn",
         "NodeStartScript": "dev",
         "NodeBuildScript": "build",
         "Regex": "dev server running at:",
         "Bundler": "ViteJs"
       },
       "/admin": {
         "DevServerAddress": "http://localhost:9000/",
         "SpaRootPath": "app2",
         "NodePackageManager": "Yarn",
         "NodeStartScript": "dev",
         "Regex": "Opening default browser",
         "Bundler": "ViteJs"
       }
     }
   }
   ```

4. Register **Services** and setup app e.g.:

   ```csharp
   using Mumrich.SpaDevMiddleware.Extensions;

   var builder = WebApplication.CreateBuilder(args);
   var appSettings = builder.Configuration.Get<AppSettings>();

   builder.Services.AddControllersWithViews();
   builder.Services.AddCors();
   builder.RegisterSinglePageAppDevMiddleware(appSettings);

   var app = builder.Build();

   app.UseStaticFiles();
   app.UseHttpsRedirection();
   app.UseRouting();
   app.UseCors();
   app.MapControllers();
   app.MapSinglePageApps(appSettings);

   app.Run();
   ```

5. When using **hot-reloading**, adapt your **SPA-bundling-setup** accordingly to accept the .NET-Webhost-Proxy on the correct Port. Custom-ENV-Variables can be passed via `appsettings.json` - e.g for [vite.config.ts](https://vitejs.dev/config/):

  ```ts
  // https://vitejs.dev/config/
  export default defineConfig({
    plugins: [vue()],
    server: {
      host: true,
      port: 3000,
      strictPort: true,
    },
  });
  ```

6. Run the app (`dotnet run`), navigate to the .NET-Web-host-Url and enjoy 👌!

### Troubleshooting

- See the working Example in the `WebSpaVue`-folder.

### Credits

- Thx to [aspnetcore-vueclimiddleware](https://github.com/EEParker/aspnetcore-vueclimiddleware) for providing insights how to handle console-output.
- Thx to [AspNetCore.SpaYarp](https://github.com/berhir/AspNetCore.SpaYarp) for providing idea and insights to use [YARP](https://microsoft.github.io/reverse-proxy/).
- Thx to [YARP](https://microsoft.github.io/reverse-proxy/) for providing such a wonderfull tool.
