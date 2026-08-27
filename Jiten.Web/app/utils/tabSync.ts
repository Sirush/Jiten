interface TabSyncMessage {
  type: 'TOKEN_REFRESHED' | 'TOKEN_REFRESH_STARTED' | 'TOKEN_REFRESH_FAILED' | 'LOGOUT';
  payload?: {
    accessToken?: string;
    refreshToken?: string;
    timestamp?: number;
    tabId?: string;
  };
}

type TabSyncListener = (payload: TabSyncMessage['payload']) => void;

export class TabSyncManager {
  private channel: BroadcastChannel | null = null;
  private readonly channelName = 'jiten-auth-sync';
  public readonly tabId: string;
  private listeners: Map<string, Set<TabSyncListener>> = new Map();
  private useFallback: boolean = false;

  constructor() {
    this.tabId = `tab-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

    if (typeof BroadcastChannel === 'undefined') {
      console.warn('BroadcastChannel not supported, using localStorage fallback');
      this.useFallback = true;
      this.setupLocalStorageFallback();
    } else {
      this.channel = new BroadcastChannel(this.channelName);
      this.setupBroadcastChannel();
    }
  }

  private setupBroadcastChannel() {
    if (!this.channel) return;

    this.channel.onmessage = (event) => {
      this.handleMessage(event.data);
    };
  }

  private setupLocalStorageFallback() {
    window.addEventListener('storage', (event) => {
      if (event.key === 'jiten-auth-event' && event.newValue) {
        try {
          const message: TabSyncMessage = JSON.parse(event.newValue);
          this.handleMessage(message);
        } catch (e) {
          console.error('Failed to parse storage event', e);
        }
      }
    });
  }

  private handleMessage(message: TabSyncMessage) {
    const listeners = this.listeners.get(message.type);
    if (listeners) {
      listeners.forEach((callback) => callback(message.payload));
    }
  }

  broadcast(type: TabSyncMessage['type'], payload?: TabSyncMessage['payload']) {
    const message: TabSyncMessage = { type, payload };

    if (this.useFallback) {
      localStorage.setItem('jiten-auth-event', JSON.stringify(message));
      setTimeout(() => localStorage.removeItem('jiten-auth-event'), 100);
    } else {
      this.channel?.postMessage(message);
    }
  }

  on(type: TabSyncMessage['type'], callback: TabSyncListener) {
    if (!this.listeners.has(type)) {
      this.listeners.set(type, new Set());
    }
    this.listeners.get(type)!.add(callback);
  }

  off(type: TabSyncMessage['type'], callback: TabSyncListener) {
    const listeners = this.listeners.get(type);
    if (listeners) {
      listeners.delete(callback);
    }
  }

  destroy() {
    this.channel?.close();
    this.listeners.clear();
  }
}
