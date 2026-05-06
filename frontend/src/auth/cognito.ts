import { Amplify } from 'aws-amplify'
import {
  signIn,
  signOut,
  getCurrentUser,
  fetchAuthSession,
} from 'aws-amplify/auth'

export function configureAmplify() {
  Amplify.configure({
    Auth: {
      Cognito: {
        userPoolId:       import.meta.env.VITE_USER_POOL_ID,
        userPoolClientId: import.meta.env.VITE_USER_POOL_CLIENT_ID,
        loginWith: {
          oauth: {
            domain:            import.meta.env.VITE_COGNITO_DOMAIN,
            scopes:            ['email', 'openid', 'profile'],
            redirectSignIn:    [window.location.origin],
            redirectSignOut:   [window.location.origin],
            responseType:      'code',
          },
        },
      },
    },
  })
}

// Use the ID token: Pre-Token Generation V1 only injects custom claims
// (tenantId) into the ID token, not the access token.
export async function getAccessToken(): Promise<string> {
  const session = await fetchAuthSession()
  const token   = session.tokens?.idToken?.toString()
  if (!token) throw new Error('No ID token')
  return token
}

export { signIn, signOut, getCurrentUser }
