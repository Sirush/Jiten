export function getEnumDictionary<T>(enumType: T, getText: (key: T[keyof T]) => string): { [key: number]: string } {
  const dictionary: { [key: number]: string } = {};
  for (const type in enumType) {
    if (isNaN(Number(type))) {
      const key = enumType[type as keyof T];
      dictionary[key] = getText(key);
    }
  }
  return dictionary;
}

export function getEnumOptions<T>(
  enumType: T,
  getText: (key: T[keyof T]) => string
): {
  value: number;
  label: string;
}[] {
  const options: { value: number; label: string }[] = [];
  for (const type in enumType) {
    if (isNaN(Number(type))) {
      const key = enumType[type as keyof T];
      options.push({ value: key, label: getText(key) });
    }
  }
  return options;
}
