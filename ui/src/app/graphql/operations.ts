import { gql } from 'apollo-angular';

import type {
  DeviceSearchData,
  DeviceSearchVars,
  DeviceTimelineData,
  DeviceTimelineVars,
} from './types';

// src/testing/record-fixtures.mjs reads the two query texts below from this file (by regex), so keep
// each one a plain template literal without interpolation.

export const DEVICE_SEARCH = gql<DeviceSearchData, DeviceSearchVars>`
  query DeviceSearch($search: String, $first: Int!, $offset: Int!) {
    devices(search: $search, first: $first, offset: $offset) {
      totalCount
      items {
        id
        hostname
        os
        ipAddress
        lastSeenAt
        tenantId
      }
    }
  }
`;

/**
 * Always requests all three sections, whatever the selected user's `services` says: the subgraphs
 * decide access, and the response is the only thing the UI trusts (plan §4.5).
 */
export const DEVICE_TIMELINE = gql<DeviceTimelineData, DeviceTimelineVars>`
  query DeviceTimeline($id: ID!, $since: DateTime, $until: DateTime) {
    device(id: $id) {
      id
      hostname
      os
      ipAddress
      lastSeenAt
      tenantId
      patchEvents(since: $since, until: $until) {
        id
        occurredAt
        status
        patch {
          id
          kbId
          title
          severity
          vendor
        }
      }
      vulnerabilityEvents(since: $since, until: $until) {
        id
        occurredAt
        kind
        findingState
        findingId
        cve {
          id
          title
          cvssScore
          severity
        }
      }
      installEvents(since: $since, until: $until) {
        id
        occurredAt
        action
        result
        software {
          name
          version
          publisher
        }
      }
    }
  }
`;
