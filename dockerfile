FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /source

COPY *.sln .
COPY src/Api/*.csproj ./src/Api/
RUN dotnet restore

COPY src/Api/. ./src/Api/
WORKDIR /source/src/Api
RUN dotnet publish -c release -o /dist --no-restore

RUN dotnet dev-certs https -ep /https/aspnetapp.pfx -p dev

FROM mcr.microsoft.com/dotnet/aspnet:7.0
COPY --from=build /dist/* /app/
COPY --from=build /https/* /https/
WORKDIR /app
ENTRYPOINT ["dotnet", "Api.dll"]