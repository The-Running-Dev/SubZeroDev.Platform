# The game-api stacks: Platform's edge in Terminate mode in front of game-service, at
# game-api.subzerodev.com, through modules/app of SubZeroDev.Infra. Two Portainer Git stacks: game-api
# (the edge and game-service, both pinned by every deploy) and game-api-platform (Postgres and the
# tunnel connector, which deploys never restart). Apply deploy/adventures-edge first: it owns this
# repository's production environment and INFRA_DISPATCH_TOKEN. Run through ./tofu.ps1; see README.md.
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

  name          = "game-api" # Stack and Compose project name; never changes
  repository    = "The-Running-Dev/SubZeroDev.Platform"
  compose_files = ["deploy/game-api/compose.yaml"]

  routes      = { game-api = "http://game-edge:8080" }
  health_path = "/health/deployed" # reports the commit once the edge reaches game-service

  # Both images come from one commit and deploy together: container.yml sends both digests.
  image = {
    variable = "GAME_EDGE_IMAGE"
    initial  = var.initial_images.game_edge
  }
  additional_images = [{
    variable = "GAME_SERVICE_IMAGE"
    initial  = var.initial_images.game_service
  }]

  # GAME_API_HANDOFF_ISSUER, _AUDIENCE, _KEY_ID, optionally _LIFETIME; GAME_API_TOKEN_ISSUER, _JWKS,
  # _AUDIENCE; GAME_API_VERIFICATION_KEY_PEM_B64 (the public half of the handoff key, not secret).
  env = var.env

  # GAME_API_SIGNING_KEY_PEM_B64, written by tools/New-SigningKey.ps1 -App game-api (SubZeroDev.Infra).
  secrets = var.secrets

  # game-service's database password, created once; Postgres in the platform stack starts with it.
  generated = ["POSTGRES_PASSWORD"]

  platform = {
    name          = "game-api-platform"
    compose_files = ["deploy/game-api/compose.platform.yaml"]
    shared_env    = ["POSTGRES_PASSWORD"]
  }

  deploy_switch       = "DEPLOY_GAME_API_ENABLED"
  repository_settings = false # deploy/adventures-edge owns the environment and INFRA_DISPATCH_TOKEN

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
  value = "https://game-api.subzerodev.com"
}
