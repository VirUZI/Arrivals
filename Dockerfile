FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Arrivals/Arrivals.csproj src/Arrivals/
RUN dotnet restore src/Arrivals/Arrivals.csproj
COPY . .
RUN dotnet publish src/Arrivals/Arrivals.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Arrivals.dll"]
