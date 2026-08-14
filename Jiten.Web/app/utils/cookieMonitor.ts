export class CookieMonitor {
  private lastTokenValue: string | null = null;
  private lastRefreshTokenValue: string | null = null;
  private pollingInterval: number | null = null;
  private onChangeCallback: ((tokens: { token: string | null, refreshToken: string | null }) => void) | null = null;

  constructor(private useBroadcastChannel: boolean) {
    if (!useBroadcastChannel) {
      this.startPolling();
    }

    document.addEventListener('visibilitychange', this.handleVisibilityChange);
  }

  private getCookie(name: string): string | null {
    if (typeof document === 'undefined') return null;

    const value = `; ${document.cookie}`;
    const parts = value.split(`; ${name}=`);
    if (parts.length === 2) {
      const cookieValue = parts.pop()?.split(';').shift();
      if (!cookieValue) return null;
      try {
        return decodeURIComponent(cookieValue);
      } catch {
        return cookieValue;
      }
    }
    return null;
  }

  private checkCookies() {
    const token = this.getCookie('token');
    const refreshToken = this.getCookie('refreshToken');

    if (token !== this.lastTokenValue || refreshToken !== this.lastRefreshTokenValue) {
      this.lastTokenValue = token;
      this.lastRefreshTokenValue = refreshToken;
      this.onChangeCallback?.({ token, refreshToken });
    }
  }

  private handleVisibilityChange = () => {
    if (!document.hidden) {
      this.checkCookies();
    }
  }

  private startPolling() {
    this.pollingInterval = window.setInterval(() => {
      this.checkCookies();
    }, 5000);
  }

  onChange(callback: (tokens: { token: string | null, refreshToken: string | null }) => void) {
    this.onChangeCallback = callback;
    this.lastTokenValue = this.getCookie('token');
    this.lastRefreshTokenValue = this.getCookie('refreshToken');
  }

  destroy() {
    if (this.pollingInterval !== null) {
      clearInterval(this.pollingInterval);
    }
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);
  }
}
