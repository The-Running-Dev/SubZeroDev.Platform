variable "state_passphrase" {
  description = "Encrypts the state and plans stored in R2."
  type        = string
  sensitive   = true
}

variable "portainer_url" {
  type    = string
  default = "https://containers.subzerodev.com"
}

variable "portainer_api_key" {
  type      = string
  sensitive = true
}

variable "cf_access_client_id" {
  description = "Cloudflare Access service token in front of Portainer."
  type        = string
  sensitive   = true
}

variable "cf_access_client_secret" {
  type      = string
  sensitive = true
}

variable "initial_images" {
  description = "Images the stack is created with, both from one main build of container.yml and pinned by digest. Deploys own them afterwards."
  type = object({
    game_edge    = string
    game_service = string
  })

  validation {
    condition     = can(regex("^ghcr\\.io/the-running-dev/platform-game-edge@sha256:[0-9a-f]{64}$", var.initial_images.game_edge))
    error_message = "initial_images.game_edge must be ghcr.io/the-running-dev/platform-game-edge pinned by digest."
  }
  validation {
    condition     = can(regex("^ghcr\\.io/the-running-dev/platform-game-service@sha256:[0-9a-f]{64}$", var.initial_images.game_service))
    error_message = "initial_images.game_service must be ghcr.io/the-running-dev/platform-game-service pinned by digest."
  }
}

variable "env" {
  description = "Plain stack values (private var-file): the handoff and issuer settings, and the handoff verification key."
  type        = map(string)

  validation {
    condition = alltrue([for k in [
      "GAME_API_HANDOFF_ISSUER", "GAME_API_HANDOFF_AUDIENCE", "GAME_API_HANDOFF_KEY_ID",
      "GAME_API_TOKEN_ISSUER", "GAME_API_TOKEN_JWKS", "GAME_API_TOKEN_AUDIENCE",
      "GAME_API_VERIFICATION_KEY_PEM_B64",
    ] : contains(keys(var.env), k)])
    error_message = "env needs GAME_API_HANDOFF_ISSUER, _AUDIENCE and _KEY_ID, GAME_API_TOKEN_ISSUER, _JWKS and _AUDIENCE, and GAME_API_VERIFICATION_KEY_PEM_B64."
  }
  validation {
    condition     = can(regex("^[A-Za-z0-9._-]+$", lookup(var.env, "GAME_API_HANDOFF_KEY_ID", ""))) && !startswith(lookup(var.env, "GAME_API_HANDOFF_KEY_ID", "."), ".")
    error_message = "GAME_API_HANDOFF_KEY_ID must be letters, digits, '.', '_' or '-', not starting with '.': it names game-service's key file."
  }
}

variable "secrets" {
  description = "Secret stack values (private var-file)."
  type        = map(string)
  sensitive   = true

  validation {
    condition     = contains(keys(var.secrets), "GAME_API_SIGNING_KEY_PEM_B64")
    error_message = "secrets needs GAME_API_SIGNING_KEY_PEM_B64: run tools/New-SigningKey.ps1 -App game-api in SubZeroDev.Infra."
  }
}
