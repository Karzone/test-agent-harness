Feature: Basket

  Background:
    Given the shop is open

  Scenario: Adding an item shows it in the basket
    When I add "Apple" to the basket
    Then the basket count is 1

  Scenario: Basket total adds up
    When I add "Apple" to the basket
    And I add "Coffee" to the basket
    Then the basket total is "£3.50"

  Scenario: Discount code reduces the total
    When I add "Coffee" to the basket
    And I apply the discount code "SAVE10"
    Then the basket total is "£2.70"
