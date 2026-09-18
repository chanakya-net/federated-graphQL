// Apollo Client 4 types results by the declared default error policy. core/graphql.provider.ts sets
// `errorPolicy: 'all'` for watchQuery and query, so `data` and `error` can both be present.
import '@apollo/client';

declare module '@apollo/client' {
  export namespace ApolloClient {
    export namespace DeclareDefaultOptions {
      interface WatchQuery {
        errorPolicy: 'all';
      }
      interface Query {
        errorPolicy: 'all';
      }
    }
  }
}
