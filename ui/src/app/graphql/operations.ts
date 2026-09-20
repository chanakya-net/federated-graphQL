import { gql } from 'apollo-angular';

import type {
  SearchCapabilitiesData,
  SearchCatalogData,
  SearchCatalogVars,
  FindDevicesData,
  FindDevicesVars,
  DeviceSearchData,
  DeviceSearchVars,
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

// --- Find devices: provider metadata/catalog and one complete result query.
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
