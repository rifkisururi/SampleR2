# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["SampleR2.csproj", "."]
RUN dotnet restore "./SampleR2.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./SampleR2.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./SampleR2.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
# COPY harus sebagai root; setelah itu siapkan folder writable untuk upload
# local storage & benchmark, lalu kembali ke user non-root ($APP_UID).
USER root
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/wwwroot/uploads/produk /app/wwwroot/uploads-benchmark \
    && chown -R $APP_UID:$APP_UID /app/wwwroot
USER $APP_UID
ENTRYPOINT ["dotnet", "SampleR2.dll"]