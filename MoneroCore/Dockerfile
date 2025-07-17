# Stage 1: Build your .NET API
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj and restore first for better caching
COPY *.csproj ./
RUN dotnet restore

# Copy all source files and publish
COPY . ./
RUN dotnet publish -c Release -o out

# Stage 2: Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copy published API files
COPY --from=build /app/out ./

# Copy your existing UnitTest executable into container root
COPY UnitTest ./
RUN chmod +x ./UnitTest

# Set ASP.NET Core to listen on port 80
ENV ASPNETCORE_URLS=http://+:80

# Expose port
EXPOSE 80

# Start your API
ENTRYPOINT ["dotnet", "MoneroCore.dll"]
