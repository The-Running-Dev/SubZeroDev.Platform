# The adventures-edge stack: Platform's edge in PassThrough mode in front of Adventures, through
# modules/app of SubZeroDev.Infra. It creates the Portainer Git stack, a Cloudflare tunnel and DNS
# record for the edge's own health hostname, and, as the call that owns this repository's settings,
# the GitHub `production` environment with INFRA_DISPATCH_TOKEN. Apply it before deploy/game-api.
# Run through ./tofu.ps1; see README.md.
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

module "app" {
  source = "git::https://github.com/The-Running-Dev/SubZeroDev.Infra.git//modules/app?ref=v2.5.0"

  name          = "adventures-edge" # Stack and Compose project name; never changes
  repository    = "The-Running-Dev/SubZeroDev.Platform"
  compose_files = ["deploy/adventures-edge/compose.yaml", "deploy/adventures-edge/compose.tunnel.yaml"]

  # Only the edge's own hostname, for the deploy's health check. Players keep reaching Adventures at
  # adventures-api.subzerodev.com, whose route belongs to the Adventures stack; pointing that route at
  # adventures-edge-ingress:8080 is the cutover, done in the Adventures repository.
  routes      = { (var.health_route) = "http://adventures-edge:8080" }
  health_path = "/health/deployed" # reports the commit once the edge reaches Adventures

  image = {
    variable = "ADVENTURES_EDGE_IMAGE"
    initial  = var.initial_image # used only when the stack is created; deploys own the image afterwards
  }

  deploy_switch       = "DEPLOY_ADVENTURES_EDGE_ENABLED"
  repository_settings = true               # this call owns the production environment and the token
  dispatch_token      = var.dispatch_token # stored as INFRA_DISPATCH_TOKEN; starts the Infra deploy workflow

  portainer_url = var.portainer_url
  portainer_credentials = {
    api_key              = var.portainer_api_key
    access_client_id     = var.cf_access_client_id
    access_client_secret = var.cf_access_client_secret
  }
  git_credentials = null # the repository is public
  registry_ids    = [1]  # the shared ghcr.io registry
}

output "public_url" {
  value = "https://${var.health_route}.subzerodev.com"
}
