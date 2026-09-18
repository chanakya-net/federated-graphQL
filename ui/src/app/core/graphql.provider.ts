import { inject } from '@angular/core';
import { InMemoryCache } from '@apollo/client';
import { provideApollo } from 'apollo-angular';
import { HttpLink } from 'apollo-angular/http';

/**
 * `errorPolicy: 'all'` is mandatory: the default `'none'` discards `data` whenever `errors[]` is
 * non-empty, so every denied or degraded section would look like a total failure.
 */
export function provideGraphql() {
  return provideApollo(() => {
    const httpLink = inject(HttpLink);
    return {
      // Angular's HttpClient does the request, so authInterceptor adds the token.
      link: httpLink.create({ uri: '/graphql' }),
      cache: new InMemoryCache(),
      defaultOptions: {
        watchQuery: { errorPolicy: 'all', fetchPolicy: 'network-only' },
        query: { errorPolicy: 'all', fetchPolicy: 'network-only' },
      },
    };
  });
}
