import type { ComputedRef, InjectionKey, Ref } from 'vue';
import type { ProfileHeader, ProfileSectionKey, ProfileViewAs } from '~/types';
import type { ProfileTab } from '~/utils/profileAppearance';

export interface ProfileContext {
  /** The resolved display name; the URL's handle only while the owner has none. */
  handle: Ref<string>;
  header: Ref<ProfileHeader | null | undefined>;
  isOwner: ComputedRef<boolean>;
  canSee: (section: ProfileSectionKey) => boolean;
  /** The audience the owner is previewing as; null on a normal view. */
  viewAs: Ref<ProfileViewAs | null>;
  /** Spread into every profile fetch's query, so a preview is answered as that audience; tabs remount when it changes. */
  viewAsQuery: ComputedRef<{ viewAs?: ProfileViewAs }>;
  /** `user/profile/{encoded handle}`, plus `/{sub}` when given; built from the URL's handle so tabs can fetch before the header resolves it. */
  apiPath: (sub?: string) => string;
  activeTab: ComputedRef<ProfileTab>;
  /** The display name, or "This member" for a profile without one. */
  name: ComputedRef<string>;
}

const profileContextKey: InjectionKey<ProfileContext> = Symbol('profile-context');
const profileHeaderReadyKey: InjectionKey<Promise<unknown>> = Symbol('profile-header-ready');

export function provideProfileContext(context: ProfileContext, headerReady: Promise<unknown>) {
  provide(profileContextKey, context);
  provide(profileHeaderReadyKey, headerReady);
}

export function useProfileContext(): ProfileContext {
  const context = inject(profileContextKey);
  if (!context) throw new Error('useProfileContext must be used under pages/profile/[username].vue');
  return context;
}

export function useProfileHeaderReady(): Promise<unknown> {
  return inject(profileHeaderReadyKey, null) ?? Promise.resolve();
}
