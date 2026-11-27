# The identity-provider stack: Keycloak at id.subzerodev.com (decided 2026-11-27), through modules/app
# of SubZeroDev.Infra. It creates the Portainer Git stack, a Cloudflare tunnel and the DNS record for
# id.subzerodev.com, and, only while an admin session's route is set, a second hostname that reaches
# Keycloak directly. Every image is third-party and pinned in compose.yaml, so nothing deploys this
# stack: it has no image variable and writes no GitHub settings. Run through ./tofu.ps1; see README.md.
terraform {
  required_version = ">= 1.10"

  required_providers {
    cloudflare = { source = "cloudflare/cloudflare", version = "5.28.1" }
    portainer  = { source = "portainer/portainer", version = "2.2.0" }
    github     = { source = "integrations/github", version = "6.13.0" }
    random     = { source = "hashicorp/random", version = "3.9.1" }
    http       = { source = "hashicorp/http", version = "3.6.3" }
  }

  # Bucket, key and endpoint come from backend.hcl (Git-ignored); R2 keys from AWS_ACCESS_KEY_ID and
  # AWS_SECRET_ACCESS_KEY, which tofu.ps1 maps from the R2_* variables.
  backend "s3" {
    region                      = "auto"
    use_lockfile                = true
    use_path_style              = true
    skip_credentials_validation = true
    skip_region_validation      = true
    skip_requesting_account_id  = true
    skip_metadata_api_check     = true
    skip_s3_checksum            = true
  }

  encryption {
    key_provider "pbkdf2" "main" {
      passphrase = var.state_passphrase
    }
    method "aes_gcm" "main" {
      keys = key_provider.pbkdf2.main
    }
    state {
      method   = method.aes_gcm.main
      enforced = true
    }
    plan {
      method   = method.aes_gcm.main
      enforced = true
    }
  }
}

provider "cloudflare" {} # CLOUDFLARE_API_TOKEN

provider "github" {
  owner = "The-Running-Dev" # GITHUB_TOKEN
}

provider "portainer" {
  endpoint = var.portainer_url
  api_key  = var.portainer_api_key
  custom_headers = {
    "CF-Access-Client-Id"     = var.cf_access_client_id
    "CF-Access-Client-Secret" = var.cf_access_client_secret
  }
}

# Keycloak's bootstrap admin. It is generated here rather than in modules/app so the operator can read
# it for an admin session (`tofu.ps1 identity-provider output -raw admin_password`). Keycloak reads it
# on the first start only; changing it afterwards changes nothing in Keycloak.
resource "random_password" "bootstrap_admin" {
  length  = 48
  special = false
}

module "app" {
  source = "git::https://github.com/The-Running-Dev/SubZeroDev.Infra.git//modules/app?ref=v2.5.0"

  name          = "identity-provider" # Stack and Compose project name; it names the volume, so it never changes
  repository    = "The-Running-Dev/SubZeroDev.Platform"
  compose_files = ["deploy/identity-provider/compose.yaml", "deploy/identity-provider/compose.tunnel.yaml"]

  # id.subzerodev.com reaches only the filtering proxy. The admin route reaches Keycloak itself and
  # exists only during an admin session (README.md, "Admin sessions"); adding or removing it changes
  # the tunnel and DNS, not the stack.
  routes = merge(
    { id = "http://proxy:8080" },
    var.admin_route == null ? {} : { (var.admin_route) = "http://keycloak:8080" },
  )
  public_route = "id"

  image               = null  # third-party images only: no deploys, no GitHub environment or variables
  repository_settings = false # adventures-edge owns this repository's deploy settings

  generated = ["POSTGRES_PASSWORD"]
  secrets   = { KC_BOOTSTRAP_ADMIN_PASSWORD = random_password.bootstrap_admin.result }

  portainer_url = var.portainer_url
  portainer_credentials = {
    api_key              = var.portainer_api_key
    access_client_id     = var.cf_access_client_id
    access_client_secret = var.cf_access_client_secret
  }
  git_credentials = null # the repository is public
}

output "public_url" {
  value = "https://id.subzerodev.com"
}

output "admin_url" {
  description = "The admin session's hostname; null when no session is open."
  value       = var.admin_route == null ? null : "https://${var.admin_route}.subzerodev.com"
}

output "admin_password" {
  description = "Keycloak's bootstrap admin password, for KC_BOOTSTRAP_ADMIN_PASSWORD during an admin session."
  value       = random_password.bootstrap_admin.result
  sensitive   = true
}
