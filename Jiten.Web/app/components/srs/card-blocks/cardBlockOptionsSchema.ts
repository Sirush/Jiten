import type { CardBlockType } from '~/types';

export interface OptionControl {
  key: string;
  label: string;
  type: 'toggle' | 'select' | 'number' | 'text';
  /** Select choices. */
  options?: { label: string; value: string }[];
  /** Number bounds; a null-able number field clears to the registry default when emptied. */
  min?: number;
  max?: number;
  nullable?: boolean;
  /** Text bounds (characters). */
  maxlength?: number;
  placeholder?: string;
}

const SIZE_OPTIONS = [
  { label: 'Small', value: 'small' },
  { label: 'Medium', value: 'medium' },
  { label: 'Large', value: 'large' },
];

const sizeControl: OptionControl = { key: 'size', label: 'Text size', type: 'select', options: SIZE_OPTIONS };
const spoilerControl: OptionControl = { key: 'spoiler', label: 'Blur until clicked', type: 'toggle' };
const hideHeadingControl: OptionControl = { key: 'hideHeading', label: 'Hide heading', type: 'toggle' };

/**
 * Editor controls for each block type's per-instance options, rendered in the block's options popover.
 * Types absent here have no configurable options (the popover still offers the move/duplicate actions).
 */
export const blockOptionsSchema: Partial<Record<CardBlockType, OptionControl[]>> = {
  headword: [
    {
      key: 'furigana',
      label: 'Furigana',
      type: 'select',
      options: [
        { label: 'After flip', value: 'afterFlip' },
        { label: 'Always shown', value: 'shown' },
        { label: 'New cards only', value: 'newOnly' },
        { label: 'Hidden', value: 'hidden' },
      ],
    },
    sizeControl,
    { key: 'showAudioButton', label: 'Audio button', type: 'toggle' },
  ],
  exampleSentence: [
    { key: 'blur', label: 'Blur until clicked', type: 'toggle' },
    { key: 'unblurOnFlip', label: 'Reveal on flip', type: 'toggle' },
    sizeControl,
    { key: 'showSource', label: 'Show source', type: 'toggle' },
    { key: 'showActions', label: 'Show actions (audio, edit)', type: 'toggle' },
  ],
  frequencyRank: [{ key: 'onlyAfterFlip', label: 'Only after flip', type: 'toggle' }],
  definitions: [
    { key: 'maxDefinitions', label: 'Max definitions', type: 'number', min: 1, max: 50, nullable: true, placeholder: 'All' },
    sizeControl,
    spoilerControl,
  ],
  customMeaning: [sizeControl, spoilerControl],
  etymology: [spoilerControl],
  confusableReadings: [spoilerControl],
  pitchAccent: [hideHeadingControl, spoilerControl],
  kanjiBreakdown: [hideHeadingControl, spoilerControl],
  wordComposition: [hideHeadingControl, spoilerControl],
  wordUsedIn: [hideHeadingControl, spoilerControl],
  deckOccurrences: [{ key: 'collapsed', label: 'Start collapsed', type: 'toggle' }],
  cardImage: [
    {
      key: 'layout',
      label: 'Layout',
      type: 'select',
      options: [
        { label: 'Beside word', value: 'beside' },
        { label: 'Free placement', value: 'below' },
      ],
    },
    { key: 'blur', label: 'Blur until flip', type: 'toggle' },
  ],
  divider: [
    {
      key: 'style',
      label: 'Style',
      type: 'select',
      options: [
        { label: 'Line', value: 'line' },
        { label: 'Space', value: 'space' },
      ],
    },
    { key: 'label', label: 'Label', type: 'text', maxlength: 40, placeholder: 'None' },
  ],
};
