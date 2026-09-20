import { gql } from 'apollo-angular';

import type {
  CatalogVars,
  SearchCapabilitiesData,
  SearchCatalogData,
  SearchCatalogVars,
  FindDevicesData,
  FindDevicesVars,
  CveCatalogData,
  DeviceSearchData,
  DeviceSearchVars,
  DeviceTimelineData,
  DeviceTimelineVars,
  PatchCatalogData,
  SoftwareCatalogData,
} from './types';

// src/testing/record-fixtures.mjs reads the query texts below from this file (by regex), so keep
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

// --- Find devices: independent catalogs and one complete result query.

export const PATCH_CATALOG = gql<PatchCatalogData, CatalogVars>`
  query PatchCatalog($search: String, $first: Int!) {
    patches(search: $search, first: $first, offset: 0) {
      id
      kbId
      title
      severity
      vendor
    }
  }
`;

export const CVE_CATALOG = gql<CveCatalogData, CatalogVars>`
  query CveCatalog($search: String, $first: Int!) {
    cves(search: $search, first: $first, offset: 0) {
      id
      title
      cvssScore
      severity
    }
  }
`;

export const SOFTWARE_CATALOG = gql<SoftwareCatalogData, CatalogVars>`
  query SoftwareCatalog($search: String, $first: Int!) {
    software(search: $search, first: $first, offset: 0) {
      name
      version
      publisher
    }
  }
`;

/** One request for the final search page; catalogs remain independent. */
export const FIND_DEVICES = gql<FindDevicesData, FindDevicesVars>`
  query FindDevices($filters: [DeviceSearchFilterInput!]!, $first: Int!, $offset: Int!) {
    findDevices(filters: $filters, first: $first, offset: $offset) {
      totalCount
      hasNextPage
      items {
        device {
          id
          hostname
          os
          ipAddress
          lastSeenAt
          tenantId
        }
        events {
          id
          source
          itemKey
          occurredAt
          label
          title
          subtitle
          status
          severity
        }
      }
    }
  }
`;

export const SEARCH_CAPABILITIES = gql<SearchCapabilitiesData, Record<string, never>>`
  query SearchCapabilities {
    searchCapabilities {
      category
      name
      icon
      color
      placeholder
      filterKind
      available
    }
  }
`;

export const SEARCH_CATALOG = gql<SearchCatalogData, SearchCatalogVars>`
  query SearchCatalog($category: String!, $search: String, $first: Int!) {
    searchCatalog(category: $category, search: $search, first: $first) {
      key
      label
      detail
    }
  }
`;
