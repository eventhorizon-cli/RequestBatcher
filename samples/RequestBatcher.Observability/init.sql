CREATE TABLE IF NOT EXISTS products
(
    id    integer PRIMARY KEY,
    name  text           NOT NULL,
    price numeric(10, 2) NOT NULL
);

INSERT INTO products (id, name, price)
SELECT product_id,
       format('Product %s', product_id),
       (product_id * 1.25)::numeric(10, 2)
FROM generate_series(1, 100) AS product_id
ON CONFLICT (id) DO NOTHING;
