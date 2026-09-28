// Optional dependency - only required when STORAGE_PROVIDER=azuresql
declare module 'mssql'

declare namespace Express {
	interface Request {
		user?: import('./middleware/auth.js').User
		entraClaims?: import('./services/entra-token.js').NormalizedEntraClaims
		authorizationContext?: import('./services/authorization.js').ServerAuthorizationContext
	}
}
