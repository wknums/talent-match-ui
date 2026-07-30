/// <reference types="vite/client" />

interface ImportMetaEnv {
	readonly VITE_APP_AUTH_MODE?: 'simple' | 'entra'
	readonly VITE_ENTRA_TENANT_ID?: string
	readonly VITE_ENTRA_STACK_A_CLIENT_ID?: string
	readonly VITE_ENTRA_API_APP_CLIENT_ID?: string
	readonly VITE_ENTRA_API_SCOPE?: string
	readonly VITE_TALENTMATCH_API_ORIGIN?: string
}

interface ImportMeta {
	readonly env: ImportMetaEnv
}
