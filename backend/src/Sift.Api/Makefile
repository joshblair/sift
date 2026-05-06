build-DocumentsFunction build-ChatFunction build-TenantFunction:
	dotnet publish Sift.Api.csproj -c Release -r linux-x64 --self-contained false -o /tmp/sift-dotnet-publish
	find "$(ARTIFACTS_DIR)" -mindepth 1 -delete
	cp -r /tmp/sift-dotnet-publish/. "$(ARTIFACTS_DIR)/"
