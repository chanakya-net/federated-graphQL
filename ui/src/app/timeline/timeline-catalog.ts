import type { TypedDocumentNode } from '@apollo/client';
import {
  Kind,
  OperationTypeNode,
  type DocumentNode,
  type FieldNode,
  type SelectionNode,
} from 'graphql';

import type { Device } from '../graphql/types';

export interface TimelineSource {
  id: string;
  field: string;
  name: string;
  icon: string;
  color: string;
  history: string;
  statuses: readonly string[];
  contractVersion: 1;
}

export interface TimelineCatalog {
  version: 1;
  schemaHash: string;
  sources: readonly TimelineSource[];
}

export interface TimelineDetail {
  label: string;
  value: string;
  mono: boolean;
}

export interface TimelineEventWire {
  id: string;
  occurredAt: string;
  label: string;
  title: string;
  subtitle: string;
  status: string;
  severity: string | null;
  details: TimelineDetail[];
}

export type TimelineDevice = Device & Record<string, unknown>;

export interface DeviceTimelineData {
  device: TimelineDevice | null;
}

export interface DeviceTimelineVars {
  id: string;
  since?: string | null;
  until?: string | null;
}

export interface TimelineQueryField {
  alias: string;
  source: TimelineSource;
}

export interface BuiltTimelineQuery {
  document: TypedDocumentNode<DeviceTimelineData, DeviceTimelineVars>;
  fields: readonly TimelineQueryField[];
  usesRange: boolean;
}

const GRAPHQL_NAME = /^[_A-Za-z][_0-9A-Za-z]*$/;
const MATERIAL_ICON = /^[a-z0-9_]{1,64}$/;
const HEX_COLOR = /^#[0-9a-fA-F]{6}$/;
const SHA256 = /^[0-9a-fA-F]{64}$/;
const CONTROL = /[\u0000-\u001f\u007f]/;

export function validateTimelineCatalog(value: unknown): TimelineCatalog {
  const catalog = record(value, 'Timeline catalog');
  if (catalog['version'] !== 1)
    throw new Error(`Unsupported timeline catalog version ${String(catalog['version'])}.`);
  if (typeof catalog['schemaHash'] !== 'string' || !SHA256.test(catalog['schemaHash']))
    throw new Error('Invalid timeline catalog: schemaHash must be a SHA256 hex string.');
  if (!Array.isArray(catalog['sources']))
    throw new Error('Invalid timeline catalog: sources must be an array.');

  const ids = new Set<string>();
  const fields = new Set<string>();
  const sources = catalog['sources'].map((raw, index) => {
    const source = record(raw, `Timeline source ${index}`);
    const id = textValue(source, 'id', index, 100);
    if (ids.has(id)) throw new Error(`Invalid timeline catalog: duplicate source id "${id}".`);
    ids.add(id);

    const field = textValue(source, 'field', index, 100);
    if (!GRAPHQL_NAME.test(field) || field.startsWith('__'))
      throw new Error(`Timeline source "${id}" has invalid GraphQL field "${field}".`);
    if (fields.has(field))
      throw new Error(`Invalid timeline catalog: duplicate source field "${field}".`);
    fields.add(field);

    const name = textValue(source, 'name', index, 100);
    const icon = textValue(source, 'icon', index, 64);
    if (!MATERIAL_ICON.test(icon))
      throw new Error(`Timeline source "${id}" has invalid icon "${icon}".`);
    const color = textValue(source, 'color', index, 7);
    if (!HEX_COLOR.test(color))
      throw new Error(`Timeline source "${id}" has invalid color "${color}".`);
    const history = textValue(source, 'history', index, 100);
    if (source['contractVersion'] !== 1)
      throw new Error(
        `Timeline source "${id}" uses unsupported contractVersion ${String(source['contractVersion'])}.`,
      );
    if (!Array.isArray(source['statuses']))
      throw new Error(`Timeline source "${id}" statuses must be an array.`);
    const statuses = source['statuses'].map((status, statusIndex) => {
      if (typeof status !== 'string' || !safeText(status, 64))
        throw new Error(`Timeline source "${id}" has invalid status at index ${statusIndex}.`);
      return status;
    });
    if (new Set(statuses).size !== statuses.length)
      throw new Error(`Timeline source "${id}" has duplicate statuses.`);

    return {
      id,
      field,
      name,
      icon,
      color,
      history,
      statuses,
      contractVersion: 1,
    } satisfies TimelineSource;
  });

  return { version: 1, schemaHash: catalog['schemaHash'], sources };
}

export function buildTimelineQuery(catalog: TimelineCatalog): BuiltTimelineQuery {
  const fields = catalog.sources.map((source, index) => ({
    alias: `timelineSource${index}`,
    source,
  }));
  const usesRange = fields.length > 0;
  const identity = ['id', 'hostname', 'os', 'ipAddress', 'lastSeenAt', 'tenantId'].map(field);
  const eventSelection = [
    'id',
    'occurredAt',
    'label',
    'title',
    'subtitle',
    'status',
    'severity',
  ].map(field);
  eventSelection.push({
    kind: Kind.FIELD,
    name: name('details'),
    selectionSet: {
      kind: Kind.SELECTION_SET,
      selections: ['label', 'value', 'mono'].map(field),
    },
  });
  const timelineFields: FieldNode[] = fields.map(({ alias, source }) => ({
    kind: Kind.FIELD,
    alias: name(alias),
    name: name(source.field),
    arguments: [
      { kind: Kind.ARGUMENT, name: name('since'), value: variable('since') },
      { kind: Kind.ARGUMENT, name: name('until'), value: variable('until') },
    ],
    selectionSet: { kind: Kind.SELECTION_SET, selections: eventSelection },
  }));
  const variableDefinitions = [
    {
      kind: Kind.VARIABLE_DEFINITION as const,
      variable: variable('id'),
      type: {
        kind: Kind.NON_NULL_TYPE as const,
        type: { kind: Kind.NAMED_TYPE as const, name: name('ID') },
      },
    },
    ...(usesRange
      ? ['since', 'until'].map((variableName) => ({
          kind: Kind.VARIABLE_DEFINITION as const,
          variable: variable(variableName),
          type: { kind: Kind.NAMED_TYPE as const, name: name('DateTime') },
        }))
      : []),
  ];
  const document: DocumentNode = {
    kind: Kind.DOCUMENT,
    definitions: [
      {
        kind: Kind.OPERATION_DEFINITION,
        operation: OperationTypeNode.QUERY,
        name: name('DeviceTimeline'),
        variableDefinitions,
        selectionSet: {
          kind: Kind.SELECTION_SET,
          selections: [
            {
              kind: Kind.FIELD,
              name: name('device'),
              arguments: [{ kind: Kind.ARGUMENT, name: name('id'), value: variable('id') }],
              selectionSet: {
                kind: Kind.SELECTION_SET,
                selections: [...identity, ...timelineFields] as SelectionNode[],
              },
            },
          ],
        },
      },
    ],
  };
  return {
    document: document as TypedDocumentNode<DeviceTimelineData, DeviceTimelineVars>,
    fields,
    usesRange,
  };
}

function record(value: unknown, label: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value))
    throw new Error(`Invalid timeline catalog: ${label} must be an object.`);
  return value as Record<string, unknown>;
}

function textValue(
  source: Record<string, unknown>,
  key: string,
  index: number,
  max: number,
): string {
  const value = source[key];
  if (typeof value !== 'string' || !safeText(value, max))
    throw new Error(`Timeline source ${index} has invalid ${key}.`);
  return value;
}

function safeText(value: string, max: number): boolean {
  return value.length > 0 && value.length <= max && value === value.trim() && !CONTROL.test(value);
}

function name(value: string) {
  return { kind: Kind.NAME as const, value };
}

function variable(value: string) {
  return { kind: Kind.VARIABLE as const, name: name(value) };
}

function field(value: string): FieldNode {
  return { kind: Kind.FIELD, name: name(value) };
}
