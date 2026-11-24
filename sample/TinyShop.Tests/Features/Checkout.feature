Feature: Checkout

  Scenario: Checking out shows a confirmation
    Given the shop is open
    When I add "Bread" to the basket
    And I check out
    Then I see the confirmation "Order placed: 1 item, total £1.20"
