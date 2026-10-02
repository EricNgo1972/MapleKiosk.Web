# Runtime image for MapleKiosk.Web (the maplekiosk.ca website), built from the framework-dependent
# publish output produced by .github/workflows/release_container.yml. The build happens in the workflow;
# this Dockerfile only packages the result, so its build context is ./publish.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# The provisioner mounts the tenant's persistent volume at /data. The only state this site keeps on disk is
# the ASP.NET DataProtection key ring (it encrypts the sign-in cookie), which defaults to
# $HOME/.aspnet/DataProtection-Keys — /root/.aspnet in this image. Symlink it onto the volume so signed-in
# users stay signed in across redeploys. Everything else (onboarding, catalog, config) lives in Azure Storage.
RUN mkdir -p /data /root && ln -s /data /root/.aspnet

# Build context is the published app (see workflow: docker build -f Dockerfile ... ./publish).
COPY . ./

# Listen on 8080 inside the container — the port the provisioner maps to and health-checks (/health).
# appsettings.json pins Kestrel to 0.0.0.0:5500 for the phoebus systemd deploy; this env var overrides it.
ENV Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "MapleKiosk.Web.dll"]
