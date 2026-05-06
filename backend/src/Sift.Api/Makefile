build-DocumentsFunction build-ChatFunction build-TenantFunction:
	dotnet publish Sift.Api.csproj \
	  -c Release \
	  -r linux-x86_64 \
	  --self-contained false \
	  -o "$(ARTIFACTS_DIR)"
