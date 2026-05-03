import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection, provideAppInitializer, inject } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptorsFromDi, withInterceptors } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideToastr } from 'ngx-toastr';
import { environment } from '../environments/environment';
import { MSAL_GUARD_CONFIG, MSAL_INSTANCE, MsalBroadcastService, MsalGuard, MsalService } from '@azure/msal-angular';
import { InteractionType, PublicClientApplication, IPublicClientApplication } from '@azure/msal-browser';
import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';


/**
 * Factory function to create and configure the MSAL (Microsoft Authentication Library) instance
 * This sets up the Azure AD authentication client with your app's credentials
 * 
 * @returns IPublicClientApplication - Configured MSAL client instance
 */
export function MSALInstanceFactory(): IPublicClientApplication {
  return new PublicClientApplication({
    auth: {
      clientId: environment.clientId,                    // Your Azure AD Application (client) ID
      authority: `https://login.microsoftonline.com/${environment.tenantId}`,  // Azure AD tenant endpoint
      redirectUri: environment.redirectURL,              // Where to redirect after successful login
      postLogoutRedirectUri: environment.postLogoutRedirectUri,  // Where to redirect after logout
    },
    cache: {
      cacheLocation: 'localStorage',                     // Store authentication tokens in browser's localStorage (persists across page refreshes)
    },
  });
}

/**
 * Factory function to configure how MSAL Guard handles user authentication
 * 
 * @returns Configuration object for MSAL Guard
 */
export function MSALGuardConfigFactory() {
  return {
    interactionType: InteractionType.Redirect,  // Use full-page redirect for login (alternative is popup)
    authRequest: {
      scopes: ['User.Read']  // Request these permissions during login
    },
    loginFailedRoute: '/'  // Redirect to landing page if login fails (prevents redirect loop)
  };
}

/**
 * Main application configuration object
 * Defines all the providers (services) that Angular will use throughout the app
 */
export const appConfig: ApplicationConfig = {
  providers: [
    // Listens for global errors across the entire application
    provideBrowserGlobalErrorListeners(),
    
    // Enables Angular zoneless change detection
    provideZonelessChangeDetection(),
    
    // Sets up routing based on the routes defined in app.routes.ts
    provideRouter(routes),
    
    // Enables HTTP requests
    provideHttpClient(
      withInterceptors([authInterceptor])
    ),

    // Enables animations for toastr and other Angular animations
    provideAnimations(),

    // Configures ngx-toastr for toast notifications
    provideToastr({
      timeOut: 3000,
      positionClass: 'toast-top-right',
      preventDuplicates: true,
      progressBar: true,
      closeButton: true,
      newestOnTop: true,
      tapToDismiss: true,
      enableHtml: true
    }),
    
    // Provides the MSAL instance using the factory function above
    { provide: MSAL_INSTANCE, useFactory: MSALInstanceFactory },
    
    // Provides the MSAL Guard configuration (how authentication is handled on protected routes)
    { provide: MSAL_GUARD_CONFIG, useFactory: MSALGuardConfigFactory },
    
    MsalService,           // Main service for login, logout, and token management
    MsalGuard,            // Route guard that protects routes requiring authentication
    MsalBroadcastService, // Broadcasts authentication events (login success, logout, etc.)
    /**
     * Initializes MSAL before the Angular app starts
     * This ensures the authentication library is ready before any components load
     * Returns a Promise that Angular waits for before bootstrapping the app
     */
    provideAppInitializer(() => {
      // Use Angular's inject() to get the properly configured MsalService instance
      const msal = inject(MsalService);
      // Initialize MSAL and return the Promise (Angular waits for this to complete)
      return msal.instance.initialize();
    }),
  ],
};
