FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

# Copy the source tree (keeping the src/ subdirectory so relative package
# references like Sharplet.Core's ../..\/Readme.md resolve as in the repo)
COPY ./src /App/src
COPY Readme.md /App/Readme.md
# Restore as distinct layers
RUN dotnet restore src
# Publish only the host project so the runtime image does not ship the
# CSR tool or the other solution binaries
RUN dotnet publish src/Sharplet.Samplekubelet -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /App
COPY --from=build-env /App/out .
ENTRYPOINT ["dotnet", "Sharplet.Samplekubelet.dll"]
