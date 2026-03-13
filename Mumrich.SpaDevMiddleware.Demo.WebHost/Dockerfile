#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["nuget.config", "."]
COPY ["Mumrich.SpaDevMiddleware.Demo.WebHost/Mumrich.SpaDevMiddleware.Demo.WebHost.csproj", "Mumrich.SpaDevMiddleware.Demo.WebHost/"]
RUN dotnet restore "Mumrich.SpaDevMiddleware.Demo.WebHost/Mumrich.SpaDevMiddleware.Demo.WebHost.csproj"
COPY . .
WORKDIR "/src/Mumrich.SpaDevMiddleware.Demo.WebHost"
RUN dotnet build "Mumrich.SpaDevMiddleware.Demo.WebHost.csproj" -c Release -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
RUN dotnet publish "Mumrich.SpaDevMiddleware.Demo.WebHost.csproj" -c Release -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Mumrich.SpaDevMiddleware.Demo.WebHost.dll"]